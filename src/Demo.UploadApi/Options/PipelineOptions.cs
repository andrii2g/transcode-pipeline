using Demo.Contracts.Enums;

namespace Demo.UploadApi.Options;

public sealed class MediaPipelineOptions
{
    public const string SectionName = "MediaPipeline";
    public UploadProviderKind UploadProvider { get; init; } = UploadProviderKind.LocalHttp;
    public TranscodeProviderKind TranscodeProvider { get; init; } = TranscodeProviderKind.FFmpeg;
    public string DefaultProfile { get; init; } = "web-standard-v1";
    public bool EnableManualTranscodeEndpoint { get; init; }
}

public sealed class UploadPolicyOptions
{
    public const string SectionName = "UploadPolicy";
    public long DefaultMaxSizeBytes { get; init; } = 20L * 1024 * 1024;
    public long AbsoluteMaxSizeBytes { get; init; } = 200L * 1024 * 1024;
    public int SessionExpirationMinutes { get; init; } = 15;
    public IReadOnlyDictionary<string, long> NamedLimits { get; init; }
        = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Profiles { get; init; } = ["web-standard-v1"];
}

public sealed class S3StorageOptions
{
    public const string SectionName = "S3Storage";
    public string Region { get; init; } = string.Empty;
    public string InputBucket { get; init; } = string.Empty;
    public string OutputBucket { get; init; } = string.Empty;
    public string UploadPrefix { get; init; } = "uploads";
    public string OutputPrefix { get; init; } = "outputs";
    public int PresignedPostExpirationMinutes { get; init; } = 15;
    public bool RequireDeclaredSizeMatch { get; init; } = true;
    public bool DeleteRejectedObjects { get; init; }
    public string? ServerSideEncryption { get; init; } = "AES256";
}

public sealed class LocalStorageOptions
{
    public const string SectionName = "LocalStorage";
    public string PublicBaseUrl { get; init; } = "http://localhost:5080";
    public string TemporaryUploadDirectory { get; init; } = "data/temp/uploads";
    public string SourceDirectory { get; init; } = "data/source";
    public string TemporaryOutputDirectory { get; init; } = "data/temp/transcodes";
    public string OutputDirectory { get; init; } = "data/output";
    public bool RequireDeclaredSizeMatch { get; init; } = true;
    public long MinimumFreeSpaceBytes { get; init; }
    public int MaximumConcurrentUploads { get; init; } = 4;
}

public sealed class WorkflowStoreOptions
{
    public const string SectionName = "WorkflowStore";
    public string Provider { get; init; } = "Sqlite";
    public string ConnectionString { get; init; } = "Data Source=data/workflows.db";
}

public sealed class AwsNotificationOptions
{
    public const string SectionName = "AwsNotifications";
    public string Region { get; init; } = string.Empty;
    public string UploadTopicArn { get; init; } = string.Empty;
    public string MediaConvertTopicArn { get; init; } = string.Empty;
    public int CertificateCacheMinutes { get; init; } = 60;
    public int MaximumMessageAgeMinutes { get; init; } = 15;
    public long RequestBodyLimitBytes { get; init; } = 524_288;
}

public sealed class FfmpegOptions
{
    public const string SectionName = "Ffmpeg";
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string FfprobePath { get; init; } = "ffprobe";
    public int MaximumConcurrentProcesses { get; init; } = 2;
    public int ProcessTimeoutMinutes { get; init; } = 120;
    public int HlsSegmentSeconds { get; init; } = 6;
    public bool KeepFailedOutput { get; init; }
    public int MaximumCapturedErrorBytes { get; init; } = 65_536;
}

public sealed class TranscodeDispatcherOptions
{
    public const string SectionName = "TranscodeDispatcher";
    public int NotificationCapacity { get; init; } = 256;
    public int ScanIntervalSeconds { get; init; } = 2;
    public int ClaimTimeoutMinutes { get; init; } = 5;
    public int MaximumConcurrentJobs { get; init; } = 2;
    public string? InstanceId { get; init; }
}

public sealed class MediaConvertOptions
{
    public const string SectionName = "MediaConvert";
    public string Region { get; init; } = string.Empty;
    public string RoleArn { get; init; } = string.Empty;
    public string? QueueArn { get; init; }
    public string WorkflowName { get; init; } = "Demo.Video.Transcode";
    public string JobTemplateName { get; init; } = "Demo-Web-Transcode-v1";
    public int StatusUpdateIntervalSeconds { get; init; } = 10;
}
