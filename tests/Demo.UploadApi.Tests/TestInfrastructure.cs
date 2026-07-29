using System.Security.Cryptography.X509Certificates;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Application;
using Demo.UploadApi.Infrastructure.Aws;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Tests;

internal sealed class SqliteHarness : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"transcode-pipeline-tests-{Guid.NewGuid():N}");
    public SqliteWorkflowStore Store { get; }

    public SqliteHarness()
    {
        Directory.CreateDirectory(Root);
        Store = new SqliteWorkflowStore(Microsoft.Extensions.Options.Options.Create(new WorkflowStoreOptions
        {
            ConnectionString = $"fake-Data Source={Path.Combine(Root, "workflows.db")}"
        }));
    }

    public async Task InitializeAsync() => await Store.InitializeAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    public static VideoWorkflow Workflow(
        string? videoId = null,
        UploadProviderKind upload = UploadProviderKind.LocalHttp,
        TranscodeProviderKind transcode = TranscodeProviderKind.FFmpeg,
        long declared = 1024,
        long maximum = 20 * 1024 * 1024,
        TranscodeJobStatus status = TranscodeJobStatus.UploadPending)
    {
        var id = videoId ?? Guid.CreateVersion7().ToString();
        var source = upload == UploadProviderKind.LocalHttp
            ? new SourceLocator(upload, $"local:{id}/source.mp4", LocalRelativePath: $"{id}/source.mp4")
            : new SourceLocator(upload, $"s3://input/uploads/{id}/source/source.mp4", "input", $"uploads/{id}/source/source.mp4");
        var now = DateTimeOffset.UtcNow;
        return new VideoWorkflow
        {
            VideoId = id,
            OriginalFileName = "video.mp4",
            ContentType = "video/mp4",
            DeclaredSizeBytes = declared,
            MaximumSizeBytes = maximum,
            UploadProvider = upload,
            TranscodeProvider = transcode,
            ProfileName = "web-standard-v1",
            Status = status,
            Source = source,
            CreatedAtUtc = now,
            UploadExpiresAtUtc = now.AddMinutes(15)
        };
    }
}

internal sealed class FakeUploadProvider : IUploadProvider
{
    public UploadProviderKind Kind { get; set; } = UploadProviderKind.LocalHttp;
    public SourceObjectMetadata? Metadata { get; set; }
    public VideoWorkflow? LastWorkflow { get; private set; }

    public Task<UploadInstruction> CreateInstructionAsync(VideoWorkflow workflow, string? oneTimeToken,
        CancellationToken cancellationToken)
    {
        LastWorkflow = workflow;
        return Task.FromResult(new UploadInstruction(Kind, Kind == UploadProviderKind.LocalHttp ? "PUT" : "POST",
            new Uri("https://example.test/upload"), oneTimeToken is null
                ? new Dictionary<string, string>() : new Dictionary<string, string> { ["X-Upload-Token"] = oneTimeToken },
            new Dictionary<string, string>()));
    }

    public Task<SourceObjectMetadata> InspectAsync(VideoWorkflow workflow, CancellationToken cancellationToken) =>
        Task.FromResult(Metadata ?? new SourceObjectMetadata(workflow.Source, workflow.DeclaredSizeBytes,
            workflow.ContentType, null, null, new Dictionary<string, string>()));
}

internal sealed class FakeTranscodeProvider : ITranscodeProvider
{
    public TranscodeProviderKind Kind { get; set; } = TranscodeProviderKind.FFmpeg;
    public int Starts { get; private set; }
    public Task<TranscodeStartResult> StartAsync(VideoWorkflow workflow, CancellationToken cancellationToken)
    {
        Starts++;
        return Task.FromResult(new TranscodeStartResult(Kind == TranscodeProviderKind.MediaConvert ? "job-1" : null,
            Kind == TranscodeProviderKind.MediaConvert ? TranscodeJobStatus.Submitted : TranscodeJobStatus.Transcoding));
    }
}

internal sealed class StaticCertificateProvider(X509Certificate2 certificate) : ISnsCertificateProvider
{
    public Task<X509Certificate2> GetAsync(Uri uri, CancellationToken cancellationToken) =>
        Task.FromResult(X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert)));
}

internal sealed class AcceptCertificateChain : ISnsCertificateChainValidator
{
    public bool Validate(X509Certificate2 certificate) => true;
}

internal static class TestOptions
{
    public static IOptions<MediaPipelineOptions> Pipeline(
        UploadProviderKind upload = UploadProviderKind.LocalHttp,
        TranscodeProviderKind transcode = TranscodeProviderKind.FFmpeg) => Microsoft.Extensions.Options.Options.Create(new MediaPipelineOptions
        {
            UploadProvider = upload,
            TranscodeProvider = transcode,
            DefaultProfile = "web-standard-v1"
        });

    public static IOptions<UploadPolicyOptions> Policy(long maximum = 20 * 1024 * 1024) => Microsoft.Extensions.Options.Options.Create(new UploadPolicyOptions
    {
        DefaultMaxSizeBytes = maximum,
        AbsoluteMaxSizeBytes = 200L * 1024 * 1024,
        Profiles = ["web-standard-v1"],
        NamedLimits = new Dictionary<string, long>
        {
            ["extended"] = 50L * 1024 * 1024,
            ["large"] = 200L * 1024 * 1024
        }
    });

    public static IOptions<TranscodeDispatcherOptions> Dispatcher() => Microsoft.Extensions.Options.Options.Create(new TranscodeDispatcherOptions
    {
        NotificationCapacity = 16,
        MaximumConcurrentJobs = 2,
        ScanIntervalSeconds = 1,
        ClaimTimeoutMinutes = 5
    });

    public static UploadCompletedNotificationChannel Channel() => new(Dispatcher(), NullLogger<UploadCompletedNotificationChannel>.Instance);
}
