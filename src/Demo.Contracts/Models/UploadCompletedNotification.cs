using Demo.Contracts.Enums;

namespace Demo.Contracts.Models;

public sealed record UploadCompletedNotification(
    string NotificationId,
    string VideoId,
    UploadProviderKind UploadProvider,
    SourceLocator Source,
    DateTimeOffset OccurredAtUtc);
