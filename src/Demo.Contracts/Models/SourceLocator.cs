using Demo.Contracts.Enums;

namespace Demo.Contracts.Models;

public sealed record SourceLocator(
    UploadProviderKind Provider,
    string Identity,
    string? Bucket = null,
    string? Key = null,
    string? VersionId = null,
    string? LocalRelativePath = null);
