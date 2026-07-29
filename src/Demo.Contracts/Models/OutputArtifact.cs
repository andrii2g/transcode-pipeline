namespace Demo.Contracts.Models;

public sealed record OutputArtifact(
    string ArtifactId,
    string VideoId,
    string Kind,
    string Name,
    string Location,
    string? ContentType,
    long? SizeBytes,
    DateTimeOffset CreatedAtUtc);
