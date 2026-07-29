using Demo.Contracts.Enums;

namespace Demo.Contracts.Models;

public sealed record UploadInstruction(
    UploadProviderKind Provider,
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> FormFields);
