using System.Text.Json.Serialization;
using Amazon;
using Amazon.MediaConvert;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Demo.Contracts.Enums;
using Demo.UploadApi.Application;
using Demo.UploadApi.Infrastructure.Aws;
using Demo.UploadApi.Infrastructure.OnPrem;
using Demo.UploadApi.Infrastructure.Uploads;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;

namespace Demo.UploadApi.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMediaPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        var pipeline = configuration.GetSection(MediaPipelineOptions.SectionName).Get<MediaPipelineOptions>() ?? new();
        services.AddOptions<MediaPipelineOptions>().Bind(configuration.GetSection(MediaPipelineOptions.SectionName))
            .Validate(value => value.UploadProvider == UploadProviderKind.S3PresignedPost && value.TranscodeProvider == TranscodeProviderKind.MediaConvert ||
                value.UploadProvider == UploadProviderKind.LocalHttp && value.TranscodeProvider == TranscodeProviderKind.FFmpeg,
                "Supported workflows are S3PresignedPost/MediaConvert and LocalHttp/FFmpeg.")
            .Validate(value => !string.IsNullOrWhiteSpace(value.DefaultProfile), "MediaPipeline:DefaultProfile is required.")
            .ValidateOnStart();
        services.AddOptions<UploadPolicyOptions>().Bind(configuration.GetSection(UploadPolicyOptions.SectionName))
            .Validate(value => value.DefaultMaxSizeBytes > 0 && value.AbsoluteMaxSizeBytes >= value.DefaultMaxSizeBytes,
                "UploadPolicy size limits are invalid.")
            .Validate(value => value.SessionExpirationMinutes is > 0 and <= 1440, "UploadPolicy session expiration is invalid.")
            .Validate(value => value.Profiles.Count > 0, "UploadPolicy:Profiles must not be empty.")
            .ValidateOnStart();
        services.AddOptions<WorkflowStoreOptions>().Bind(configuration.GetSection(WorkflowStoreOptions.SectionName))
            .Validate(value => value.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase), "Only the demo Sqlite store is implemented.")
            .Validate(value => !string.IsNullOrWhiteSpace(value.ConnectionString), "WorkflowStore:ConnectionString is required.")
            .ValidateOnStart();
        services.AddOptions<TranscodeDispatcherOptions>().Bind(configuration.GetSection(TranscodeDispatcherOptions.SectionName))
            .Validate(value => value.NotificationCapacity > 0 && value.ScanIntervalSeconds > 0 &&
                value.ClaimTimeoutMinutes > 0 && value.MaximumConcurrentJobs > 0, "TranscodeDispatcher values must be positive.")
            .ValidateOnStart();
        services.AddOptions<S3StorageOptions>().Bind(configuration.GetSection(S3StorageOptions.SectionName))
            .Validate(value => pipeline.UploadProvider != UploadProviderKind.S3PresignedPost ||
                !string.IsNullOrWhiteSpace(value.Region) && !string.IsNullOrWhiteSpace(value.InputBucket) &&
                !string.IsNullOrWhiteSpace(value.OutputBucket), "S3Storage Region and buckets are required for the AWS workflow.")
            .ValidateOnStart();
        services.AddOptions<AwsNotificationOptions>().Bind(configuration.GetSection(AwsNotificationOptions.SectionName))
            .Validate(value => pipeline.UploadProvider != UploadProviderKind.S3PresignedPost ||
                !string.IsNullOrWhiteSpace(value.Region) &&
                IsTopicArn(value.UploadTopicArn) && IsTopicArn(value.MediaConvertTopicArn) &&
                !string.Equals(value.UploadTopicArn, value.MediaConvertTopicArn, StringComparison.Ordinal) &&
                value.CertificateCacheMinutes > 0 && value.MaximumMessageAgeMinutes > 0 &&
                value.RequestBodyLimitBytes is > 0 and <= 1_048_576,
                "Exact, distinct upload and MediaConvert topic ARNs are required for the AWS workflow.")
            .ValidateOnStart();
        services.AddOptions<MediaConvertOptions>().Bind(configuration.GetSection(MediaConvertOptions.SectionName))
            .Validate(value => pipeline.TranscodeProvider != TranscodeProviderKind.MediaConvert ||
                !string.IsNullOrWhiteSpace(value.Region) && !string.IsNullOrWhiteSpace(value.RoleArn) &&
                !string.IsNullOrWhiteSpace(value.JobTemplateName), "MediaConvert Region, RoleArn, and JobTemplateName are required.")
            .ValidateOnStart();
        services.AddOptions<LocalStorageOptions>().Bind(configuration.GetSection(LocalStorageOptions.SectionName))
            .Validate(value => pipeline.UploadProvider != UploadProviderKind.LocalHttp ||
                Uri.TryCreate(value.PublicBaseUrl, UriKind.Absolute, out _) && value.MaximumConcurrentUploads > 0 &&
                !string.IsNullOrWhiteSpace(value.TemporaryUploadDirectory) && !string.IsNullOrWhiteSpace(value.SourceDirectory) &&
                !string.IsNullOrWhiteSpace(value.TemporaryOutputDirectory) && !string.IsNullOrWhiteSpace(value.OutputDirectory),
                "LocalStorage paths, PublicBaseUrl, and concurrency are required for the local workflow.")
            .ValidateOnStart();
        services.AddOptions<FfmpegOptions>().Bind(configuration.GetSection(FfmpegOptions.SectionName))
            .Validate(value => pipeline.TranscodeProvider != TranscodeProviderKind.FFmpeg ||
                !string.IsNullOrWhiteSpace(value.FfmpegPath) && !string.IsNullOrWhiteSpace(value.FfprobePath) &&
                value.MaximumConcurrentProcesses > 0, "FFmpeg executable paths and concurrency are required.")
            .ValidateOnStart();

        services.ConfigureHttpJsonOptions(value => value.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IWorkflowStore, SqliteWorkflowStore>();
        services.AddHostedService<WorkflowStoreInitializer>();
        services.AddSingleton<IUploadLimitResolver, UploadLimitResolver>();
        services.AddSingleton<ICreateUploadSessionService, CreateUploadSessionService>();
        services.AddSingleton<UploadCompletedNotificationChannel>();
        services.AddSingleton<IUploadCompletedNotificationPublisher>(provider => provider.GetRequiredService<UploadCompletedNotificationChannel>());
        services.AddSingleton<ICompatibilityTranscodeService, CompatibilityTranscodeService>();

        if (pipeline.UploadProvider == UploadProviderKind.S3PresignedPost)
        {
            var s3 = configuration.GetSection(S3StorageOptions.SectionName).Get<S3StorageOptions>()!;
            var notifications = configuration.GetSection(AwsNotificationOptions.SectionName).Get<AwsNotificationOptions>()!;
            var mediaConvert = configuration.GetSection(MediaConvertOptions.SectionName).Get<MediaConvertOptions>()!;
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(RegionEndpoint.GetBySystemName(s3.Region)));
            services.AddSingleton<IAmazonMediaConvert>(_ => new AmazonMediaConvertClient(RegionEndpoint.GetBySystemName(mediaConvert.Region)));
            services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                new AmazonSimpleNotificationServiceClient(RegionEndpoint.GetBySystemName(notifications.Region)));
            services.AddSingleton<IUploadProvider, S3PresignedPostUploadProvider>();
            services.AddSingleton<ITranscodeProvider, MediaConvertTranscodeProvider>();
            services.AddMemoryCache();
            services.AddHttpClient("sns-certificates").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
            services.AddSingleton<ISnsCertificateProvider, SnsCertificateProvider>();
            services.AddSingleton<ISnsCertificateChainValidator, SnsCertificateChainValidator>();
            services.AddSingleton<ISnsMessageSignatureVerifier, SnsMessageSignatureVerifier>();
            services.AddSingleton<ISnsSubscriptionConfirmationService, SnsSubscriptionConfirmationService>();
            services.AddSingleton<S3SnsNotificationHandler>();
            services.AddSingleton<MediaConvertSnsNotificationHandler>();
            services.AddSingleton<AwsNotificationService>();
        }
        else
        {
            services.AddSingleton<LocalPathResolver>();
            services.AddSingleton<IUploadProvider, LocalHttpUploadProvider>();
            services.AddSingleton<ILocalUploadService, LocalUploadService>();
            services.AddSingleton<IMediaProcessRunner, MediaProcessRunner>();
            services.AddSingleton<ITranscodeProvider, FfmpegTranscodeProvider>();
            services.AddHostedService<LocalUploadRecoveryWorker>();
        }
        services.AddHostedService<TranscodeDispatchWorker>();
        return services;
    }

    private static bool IsTopicArn(string value) => value.StartsWith("arn:aws:sns:", StringComparison.Ordinal) && value.Count(c => c == ':') >= 5;
}
