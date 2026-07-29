using Demo.Contracts.Enums;
using Demo.Contracts.Models;

namespace Demo.UploadApi.Persistence;

public enum LocalUploadClaimResult
{
    Claimed,
    NotFound,
    InvalidToken,
    Expired,
    AlreadyUsed,
    InvalidState
}

public enum CompletionResult
{
    Applied,
    Duplicate,
    NotFound,
    InvalidState
}

public interface IWorkflowStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task CreateAsync(VideoWorkflow workflow, UploadSession session, CancellationToken cancellationToken);
    Task<VideoWorkflow?> GetAsync(string videoId, CancellationToken cancellationToken);
    Task<UploadSession?> GetSessionAsync(string videoId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutputArtifact>> GetArtifactsAsync(string videoId, CancellationToken cancellationToken);
    Task<LocalUploadClaimResult> TryClaimLocalUploadAsync(
        string videoId,
        ReadOnlyMemory<byte> tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<CompletionResult> CompleteUploadAsync(
        string videoId,
        SourceObjectMetadata metadata,
        string sourceIdentityHash,
        DateTimeOffset occurredAtUtc,
        string? topicArn,
        string? messageId,
        string notificationType,
        CancellationToken cancellationToken);
    Task<bool> RecordNotificationAsync(
        string topicArn,
        string messageId,
        string notificationType,
        string? sourceIdentity,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken);
    Task RejectUploadAsync(
        string videoId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<VideoWorkflow>> ListUploadingAsync(int maximum, CancellationToken cancellationToken);
    Task<IReadOnlyList<VideoWorkflow>> ListDispatchableAsync(int maximum, CancellationToken cancellationToken);
    Task<VideoWorkflow?> TryClaimForDispatchAsync(
        string videoId,
        string instanceId,
        DateTimeOffset claimExpiresAtUtc,
        CancellationToken cancellationToken);
    Task<int> RecoverStaleClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task MarkValidatingAsync(string videoId, CancellationToken cancellationToken);
    Task RecordProviderStartedAsync(
        string videoId,
        string? externalJobId,
        TranscodeJobStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task UpdateProviderStatusAsync(
        string videoId,
        string? expectedExternalJobId,
        TranscodeJobStatus status,
        double? progressPercent,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset occurredAtUtc,
        IReadOnlyList<OutputArtifact>? artifacts,
        CancellationToken cancellationToken);
}
