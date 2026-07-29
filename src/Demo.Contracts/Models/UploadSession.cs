namespace Demo.Contracts.Models;

public sealed record UploadSession(
    string VideoId,
    byte[]? TokenHash,
    DateTimeOffset ExpiresAtUtc,
    long MaximumSizeBytes,
    long DeclaredSizeBytes,
    DateTimeOffset? ClaimedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? ProviderPayloadJson = null);
