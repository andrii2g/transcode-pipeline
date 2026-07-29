using System.Globalization;
using System.Text;
using System.Text.Json;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Application;
using Demo.UploadApi.Infrastructure.Uploads;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Infrastructure.OnPrem;

public sealed class FfmpegTranscodeProvider(
    IMediaProcessRunner runner,
    LocalPathResolver paths,
    IWorkflowStore store,
    IOptions<FfmpegOptions> options,
    TimeProvider timeProvider) : ITranscodeProvider
{
    private readonly FfmpegOptions _options = options.Value;
    public TranscodeProviderKind Kind => TranscodeProviderKind.FFmpeg;

    public async Task<TranscodeStartResult> StartAsync(VideoWorkflow workflow, CancellationToken cancellationToken)
    {
        var sourcePath = paths.SourcePath(workflow.Source.LocalRelativePath!);
        var claimId = Guid.CreateVersion7().ToString();
        var processing = paths.ProcessingOutputPath(workflow.VideoId, claimId);
        var published = paths.PublishedOutputPath(workflow.VideoId);
        Directory.CreateDirectory(processing);
        try
        {
            var duration = await ProbeAsync(sourcePath, cancellationToken);
            var now = timeProvider.GetUtcNow();
            await store.RecordProviderStartedAsync(workflow.VideoId, null, TranscodeJobStatus.Transcoding, now, cancellationToken);
            var fileDirectory = Path.Combine(processing, "file");
            var hls720Directory = Path.Combine(processing, "hls", "720p");
            var hls480Directory = Path.Combine(processing, "hls", "480p");
            Directory.CreateDirectory(fileDirectory);
            Directory.CreateDirectory(hls720Directory);
            Directory.CreateDirectory(hls480Directory);
            await RunFfmpegAsync(BuildMp4Arguments(sourcePath, Path.Combine(fileDirectory, "video.mp4")), cancellationToken);
            await RunFfmpegAsync(BuildHlsArguments(sourcePath, 720, hls720Directory), cancellationToken);
            await RunFfmpegAsync(BuildHlsArguments(sourcePath, 480, hls480Directory), cancellationToken);
            var master = """
                #EXTM3U
                #EXT-X-VERSION:3
                #EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720
                720p/index.m3u8
                #EXT-X-STREAM-INF:BANDWIDTH=1400000,RESOLUTION=854x480
                480p/index.m3u8
                """;
            await File.WriteAllTextAsync(Path.Combine(processing, "hls", "master.m3u8"), master, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(published)!);
            if (Directory.Exists(published)) throw new IOException("Published output already exists.");
            Directory.Move(processing, published);
            var completedAt = timeProvider.GetUtcNow();
            IReadOnlyList<OutputArtifact> artifacts =
            [
                Artifact(workflow.VideoId, "HlsMasterPlaylist", "master.m3u8", $"outputs/{workflow.VideoId}/hls/master.m3u8", "application/vnd.apple.mpegurl", completedAt),
                Artifact(workflow.VideoId, "Mp4", "video.mp4", $"outputs/{workflow.VideoId}/file/video.mp4", "video/mp4", completedAt)
            ];
            await store.UpdateProviderStatusAsync(workflow.VideoId, null, TranscodeJobStatus.Completed, 100,
                null, null, completedAt, artifacts, cancellationToken);
            return new TranscodeStartResult(null, TranscodeJobStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            await store.UpdateProviderStatusAsync(workflow.VideoId, null, TranscodeJobStatus.Canceled, null,
                "Canceled", "FFmpeg processing was canceled.", timeProvider.GetUtcNow(), null, CancellationToken.None);
            if (Directory.Exists(processing) && !_options.KeepFailedOutput) Directory.Delete(processing, recursive: true);
            throw;
        }
        catch (Exception exception)
        {
            await store.UpdateProviderStatusAsync(workflow.VideoId, null, TranscodeJobStatus.Failed, null,
                "FfmpegFailed", Bounded(exception.Message), timeProvider.GetUtcNow(), null, CancellationToken.None);
            if (Directory.Exists(processing) && !_options.KeepFailedOutput) Directory.Delete(processing, recursive: true);
            throw;
        }
    }

    private async Task<TimeSpan> ProbeAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_options.FfprobePath,
        [
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", sourcePath
        ], cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException($"ffprobe rejected the source: {Bounded(result.StandardError)}");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var hasVideo = document.RootElement.GetProperty("streams").EnumerateArray().Any(stream =>
            stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
        if (!hasVideo) throw new InvalidDataException("The source has no video stream.");
        var durationText = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            throw new InvalidDataException("The source has no positive duration.");
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_options.FfmpegPath, arguments, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with code {result.ExitCode}: {Bounded(result.StandardError)}");
    }

    internal static IReadOnlyList<string> BuildMp4Arguments(string source, string output) =>
    [
        "-y", "-v", "warning", "-i", source, "-map", "0:v:0", "-map", "0:a?",
        "-vf", "scale=w=-2:h='min(720,ih)':force_original_aspect_ratio=decrease",
        "-c:v", "libx264", "-preset", "medium", "-crf", "22", "-c:a", "aac", "-b:a", "128k",
        "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output
    ];

    internal IReadOnlyList<string> BuildHlsArguments(string source, int height, string directory)
    {
        var bitrate = height == 720 ? "2600k" : "1200k";
        return
        [
            "-y", "-v", "warning", "-i", source, "-map", "0:v:0", "-map", "0:a?",
            "-vf", $"scale=w=-2:h={height}:force_original_aspect_ratio=decrease",
            "-c:v", "libx264", "-preset", "medium", "-b:v", bitrate, "-maxrate", bitrate,
            "-bufsize", height == 720 ? "5200k" : "2400k", "-c:a", "aac", "-b:a", "128k",
            "-hls_time", _options.HlsSegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_playlist_type", "vod", "-hls_segment_filename", Path.Combine(directory, "segment-%05d.ts"),
            "-progress", "pipe:1", "-nostats", Path.Combine(directory, "index.m3u8")
        ];
    }

    private OutputArtifact Artifact(string videoId, string kind, string name, string location,
        string contentType, DateTimeOffset now)
    {
        var physical = kind == "Mp4"
            ? Path.Combine(paths.PublishedOutputPath(videoId), "file", name)
            : Path.Combine(paths.PublishedOutputPath(videoId), "hls", name);
        return new OutputArtifact(Guid.CreateVersion7().ToString(), videoId, kind, name, location,
            contentType, File.Exists(physical) ? new FileInfo(physical).Length : null, now);
    }

    private string Bounded(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= _options.MaximumCapturedErrorBytes) return value;
        return value[^Math.Min(value.Length, _options.MaximumCapturedErrorBytes / 2)..];
    }
}
