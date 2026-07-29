using Demo.Contracts.Enums;
using Demo.Contracts.Models;

namespace Demo.UploadApi.Application;

public interface IUploadProvider
{
    UploadProviderKind Kind { get; }
    Task<UploadInstruction> CreateInstructionAsync(
        VideoWorkflow workflow, string? oneTimeToken, CancellationToken cancellationToken);
    Task<SourceObjectMetadata> InspectAsync(VideoWorkflow workflow, CancellationToken cancellationToken);
}

public interface IUploadCompletedNotificationPublisher
{
    ValueTask PublishAsync(UploadCompletedNotification notification, CancellationToken cancellationToken);
    IAsyncEnumerable<UploadCompletedNotification> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed record TranscodeStartResult(string? ExternalJobId, TranscodeJobStatus Status);

public interface ITranscodeProvider
{
    TranscodeProviderKind Kind { get; }
    Task<TranscodeStartResult> StartAsync(VideoWorkflow workflow, CancellationToken cancellationToken);
}

public sealed class UploadRequestException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class LocalUploadException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record CreatedUpload(VideoWorkflow Workflow, UploadInstruction Instruction);

public sealed record ProviderEvent(
    string VideoId,
    string? ExternalJobId,
    TranscodeJobStatus Status,
    double? ProgressPercent,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<OutputArtifact>? Artifacts);
