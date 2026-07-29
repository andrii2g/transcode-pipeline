using Amazon;
using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;
using Amazon.Runtime;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Infrastructure.Aws;
using Demo.UploadApi.Infrastructure.OnPrem;
using Demo.UploadApi.Infrastructure.Uploads;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Tests;

public sealed class TranscodeProviderTests
{
    [Fact]
    public async Task MediaConvert_request_has_regional_template_role_destinations_metadata_and_stable_token()
    {
        using var client = new CapturingMediaConvertClient();
        var provider = new MediaConvertTranscodeProvider(client, Microsoft.Extensions.Options.Options.Create(new MediaConvertOptions
        {
            Region = "eu-west-1",
            RoleArn = "arn:aws:iam::111122223333:role/media",
            QueueArn = "queue",
            WorkflowName = "workflow",
            JobTemplateName = "template"
        }), Microsoft.Extensions.Options.Options.Create(new S3StorageOptions { OutputBucket = "output", OutputPrefix = "outputs" }));
        var workflow = SqliteHarness.Workflow(upload: UploadProviderKind.S3PresignedPost,
            transcode: TranscodeProviderKind.MediaConvert);
        var result = await provider.StartAsync(workflow, CancellationToken.None);
        var request = Assert.IsType<CreateJobRequest>(client.Request);
        Assert.Equal("arn:aws:iam::111122223333:role/media", request.Role);
        Assert.Equal("template", request.JobTemplate);
        Assert.Equal("queue", request.Queue);
        Assert.Equal(workflow.VideoId, request.UserMetadata["videoId"]);
        Assert.Equal(workflow.ProfileName, request.UserMetadata["profile"]);
        Assert.Equal(MediaConvertTranscodeProvider.StableToken(workflow), request.ClientRequestToken);
        Assert.Equal(64, request.ClientRequestToken.Length);
        Assert.Contains(request.Settings.OutputGroups, group => group.OutputGroupSettings.HlsGroupSettings?.Destination.Contains(workflow.VideoId) == true);
        Assert.Equal("job-123", result.ExternalJobId);
        Assert.Equal(RegionEndpoint.EUWest1.SystemName, client.Config.RegionEndpoint.SystemName);
    }

    [Fact]
    public void Ffmpeg_arguments_keep_untrusted_path_as_one_argument_and_progress_is_parsed()
    {
        var source = "source.mp4; touch hacked";
        var arguments = FfmpegTranscodeProvider.BuildMp4Arguments(source, "output.mp4");
        Assert.Contains(source, arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("ffmpeg ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(50, FfmpegProgressParser.ParsePercent("frame=1\nout_time_us=5000000\nprogress=continue", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Invalid_probe_marks_failed_and_removes_processing_output()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await PrepareValidatingAsync(harness);
        var local = LocalOptions(harness.Root);
        var paths = new LocalPathResolver(local);
        var source = paths.SourcePath(workflow.Source.LocalRelativePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var runner = new SequenceRunner(new ProcessResult(1, "", "invalid media"));
        var provider = new FfmpegTranscodeProvider(runner, paths, harness.Store,
            Microsoft.Extensions.Options.Options.Create(new FfmpegOptions { KeepFailedOutput = false }), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.StartAsync(workflow, CancellationToken.None));
        var failed = await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None);
        Assert.Equal(TranscodeJobStatus.Failed, failed?.Status);
        Assert.Empty(Directory.Exists(Path.Combine(harness.Root, "temp", "outputs", workflow.VideoId))
            ? Directory.GetDirectories(Path.Combine(harness.Root, "temp", "outputs", workflow.VideoId)) : []);
    }

    private static async Task<VideoWorkflow> PrepareValidatingAsync(SqliteHarness harness)
    {
        var workflow = SqliteHarness.Workflow();
        await harness.Store.CreateAsync(workflow, new UploadSession(workflow.VideoId, null,
            workflow.UploadExpiresAtUtc, workflow.MaximumSizeBytes, workflow.DeclaredSizeBytes), CancellationToken.None);
        var metadata = new SourceObjectMetadata(workflow.Source with { Identity = workflow.Source.Identity + ":done" },
            workflow.DeclaredSizeBytes, workflow.ContentType, null, "hash", new Dictionary<string, string>());
        await harness.Store.CompleteUploadAsync(workflow.VideoId, metadata, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            null, null, "test", CancellationToken.None);
        await harness.Store.TryClaimForDispatchAsync(workflow.VideoId, "test", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        await harness.Store.MarkValidatingAsync(workflow.VideoId, CancellationToken.None);
        return (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))!;
    }

    private static IOptions<LocalStorageOptions> LocalOptions(string root) => Microsoft.Extensions.Options.Options.Create(new LocalStorageOptions
    {
        PublicBaseUrl = "https://example.test",
        TemporaryUploadDirectory = Path.Combine(root, "temp", "uploads"),
        SourceDirectory = Path.Combine(root, "source"),
        TemporaryOutputDirectory = Path.Combine(root, "temp", "outputs"),
        OutputDirectory = Path.Combine(root, "output"),
        MaximumConcurrentUploads = 1
    });

    private sealed class CapturingMediaConvertClient : AmazonMediaConvertClient
    {
        public CapturingMediaConvertClient() : base(new AnonymousAWSCredentials(),
            new AmazonMediaConvertConfig { RegionEndpoint = RegionEndpoint.EUWest1 })
        { }
        public CreateJobRequest? Request { get; private set; }
        public override Task<CreateJobResponse> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new CreateJobResponse { Job = new Job { Id = "job-123" } });
        }
    }

    private sealed class SequenceRunner(params ProcessResult[] results) : IMediaProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results);
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Task.FromResult(_results.Dequeue());
    }
}
