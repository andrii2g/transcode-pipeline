using System.Diagnostics;
using System.Text;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Infrastructure.OnPrem;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IMediaProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class MediaProcessRunner : IMediaProcessRunner, IDisposable
{
    private readonly FfmpegOptions _options;
    private readonly SemaphoreSlim _capacity;

    public MediaProcessRunner(IOptions<FfmpegOptions> options)
    {
        _options = options.Value;
        _capacity = new SemaphoreSlim(_options.MaximumConcurrentProcesses, _options.MaximumConcurrentProcesses);
    }

    public async Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await _capacity.WaitAsync(cancellationToken);
        try
        {
            return await RunCoreAsync(executable, arguments, cancellationToken);
        }
        finally
        {
            _capacity.Release();
        }
    }

    private async Task<ProcessResult> RunCoreAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start '{executable}'.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(_options.ProcessTimeoutMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, 1_048_576, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, _options.MaximumCapturedErrorBytes, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    public void Dispose() => _capacity.Dispose();

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumBytes, CancellationToken cancellationToken)
    {
        var tail = new Queue<string>();
        var bytes = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            tail.Enqueue(line);
            bytes += lineBytes;
            while (bytes > maximumBytes && tail.TryDequeue(out var removed))
                bytes -= Encoding.UTF8.GetByteCount(removed) + 1;
        }
        return string.Join(Environment.NewLine, tail);
    }
}

public static class FfmpegProgressParser
{
    public static double? ParsePercent(string output, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return null;
        long? microseconds = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), out var parsed)) microseconds = parsed;
        }
        return microseconds is null ? null : Math.Clamp(microseconds.Value / duration.TotalMicroseconds * 100d, 0d, 100d);
    }
}
