using Demo.Contracts.Enums;

namespace Demo.Contracts.Models;

public sealed record VideoWorkflow
{
    public required string VideoId { get; init; }
    public required string OriginalFileName { get; init; }
    public string? ContentType { get; init; }
    public long DeclaredSizeBytes { get; init; }
    public long MaximumSizeBytes { get; init; }
    public long? ActualSizeBytes { get; init; }
    public UploadProviderKind UploadProvider { get; init; }
    public TranscodeProviderKind TranscodeProvider { get; init; }
    public required string ProfileName { get; init; }
    public TranscodeJobStatus Status { get; init; }
    public required SourceLocator Source { get; init; }
    public string? SourceETag { get; init; }
    public string? SourceChecksumSha256 { get; init; }
    public string? SourceIdentityHash { get; init; }
    public string? ExternalJobId { get; init; }
    public double? ProgressPercent { get; init; }
    public string? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimExpiresAtUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UploadExpiresAtUtc { get; init; }
    public DateTimeOffset? UploadStartedAtUtc { get; init; }
    public DateTimeOffset? UploadedAtUtc { get; init; }
    public DateTimeOffset? SubmittedAtUtc { get; init; }
    public DateTimeOffset? ProcessingStartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long RowVersion { get; init; }
}
