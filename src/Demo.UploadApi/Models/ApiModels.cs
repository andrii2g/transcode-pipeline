using System.ComponentModel.DataAnnotations;
using Demo.Contracts.Enums;

namespace Demo.UploadApi.Models;

public sealed record CreateUploadRequest(
    [property: Required, MinLength(1)] string FileName,
    string? ContentType,
    long SizeBytes,
    string? Profile);

public sealed record StartTranscodeRequest([property: Required, MinLength(1)] string VideoId);

public sealed record UploadInstructionResponse(
    UploadProviderKind Provider,
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> FormFields);

public sealed record CreateUploadResponse(
    string VideoId,
    TranscodeJobStatus Status,
    long MaximumSizeBytes,
    DateTimeOffset ExpiresAtUtc,
    UploadInstructionResponse Upload);

public sealed record TranscodeStatusResponse(
    string VideoId,
    TranscodeJobStatus Status,
    UploadProviderKind UploadProvider,
    TranscodeProviderKind TranscodeProvider,
    string Profile,
    long DeclaredSizeBytes,
    long? SourceSizeBytes,
    string? ExternalJobId,
    double? ProgressPercent,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UploadedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record OutputArtifactResponse(
    string Kind,
    string Name,
    string Location,
    string? ContentType,
    long? SizeBytes);

public sealed record TranscodeResultResponse(
    string VideoId,
    TranscodeJobStatus Status,
    IReadOnlyList<OutputArtifactResponse> Artifacts);
