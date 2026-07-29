namespace Demo.Contracts.Models;

public sealed record SourceObjectMetadata(
    SourceLocator Source,
    long SizeBytes,
    string? ContentType,
    string? ETag,
    string? ChecksumSha256,
    IReadOnlyDictionary<string, string> Metadata);
