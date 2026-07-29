using System.Text.Json;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Infrastructure.Aws;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Tests;

public sealed class AwsNotificationHandlerTests
{
    private const string Topic = "arn:aws:sns:eu-west-1:111122223333:uploads";

    [Fact]
    public async Task S3_test_event_is_acknowledged_idempotently()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var handler = Handler(harness, new FakeUploadProvider { Kind = UploadProviderKind.S3PresignedPost });
        var envelope = Envelope("{\"Service\":\"Amazon S3\",\"Event\":\"s3:TestEvent\"}");
        await handler.HandleAsync(envelope, CancellationToken.None);
        await handler.HandleAsync(envelope, CancellationToken.None);
    }

    [Fact]
    public async Task Encoded_created_key_is_inspected_and_duplicate_message_or_source_is_idempotent()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await CreateS3WorkflowAsync(harness);
        var metadata = new SourceObjectMetadata(workflow.Source with { Identity = workflow.Source.Identity + "#etag" },
            workflow.DeclaredSizeBytes, workflow.ContentType, "etag", null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["video-id"] = workflow.VideoId });
        var provider = new FakeUploadProvider { Kind = UploadProviderKind.S3PresignedPost, Metadata = metadata };
        var handler = Handler(harness, provider);
        var message = S3Message(workflow, "replace-video-input", Uri.EscapeDataString(workflow.Source.Key!));
        var envelope = Envelope(message);
        await handler.HandleAsync(envelope, CancellationToken.None);
        await handler.HandleAsync(envelope, CancellationToken.None);
        Assert.Equal(TranscodeJobStatus.Uploaded,
            (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task Wrong_bucket_is_rejected_and_oversize_or_metadata_mismatch_never_dispatches()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await CreateS3WorkflowAsync(harness);
        var provider = new FakeUploadProvider { Kind = UploadProviderKind.S3PresignedPost };
        var handler = Handler(harness, provider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.HandleAsync(
            Envelope(S3Message(workflow, "wrong", workflow.Source.Key!)), CancellationToken.None));

        provider.Metadata = new SourceObjectMetadata(workflow.Source, workflow.MaximumSizeBytes + 1, workflow.ContentType,
            null, null, new Dictionary<string, string> { ["video-id"] = workflow.VideoId });
        await handler.HandleAsync(Envelope(S3Message(workflow, "replace-video-input", workflow.Source.Key!), "oversize"), CancellationToken.None);
        Assert.Equal(TranscodeJobStatus.UploadRejected,
            (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))?.Status);
        Assert.Empty(await harness.Store.ListDispatchableAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task MediaConvert_complete_persists_artifacts_duplicate_is_idempotent_and_wrong_job_is_rejected()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await PrepareSubmittedAsync(harness);
        var handler = new MediaConvertSnsNotificationHandler(harness.Store,
            Microsoft.Extensions.Options.Options.Create(new S3StorageOptions { OutputPrefix = "outputs" }), TimeProvider.System);
        var complete = MediaConvertEnvelope(workflow.VideoId, "job-1", "COMPLETE", "event-1");
        await handler.HandleAsync(complete, CancellationToken.None);
        await handler.HandleAsync(complete, CancellationToken.None);
        Assert.Equal(TranscodeJobStatus.Completed,
            (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))?.Status);
        Assert.Equal(2, (await harness.Store.GetArtifactsAsync(workflow.VideoId, CancellationToken.None)).Count);

        var other = await PrepareSubmittedAsync(harness, "job-2");
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            MediaConvertEnvelope(other.VideoId, "wrong-job", "ERROR", "event-2"), CancellationToken.None));
    }

    private static S3SnsNotificationHandler Handler(SqliteHarness harness, FakeUploadProvider provider) => new(
        harness.Store, provider, TestOptions.Channel(), Microsoft.Extensions.Options.Options.Create(new S3StorageOptions
        {
            InputBucket = "replace-video-input",
            UploadPrefix = "uploads",
            RequireDeclaredSizeMatch = true
        }), TimeProvider.System);

    private static async Task<VideoWorkflow> CreateS3WorkflowAsync(SqliteHarness harness)
    {
        var workflow = SqliteHarness.Workflow(upload: UploadProviderKind.S3PresignedPost,
            transcode: TranscodeProviderKind.MediaConvert);
        workflow = workflow with
        {
            Source = new SourceLocator(UploadProviderKind.S3PresignedPost,
                $"s3://replace-video-input/uploads/{workflow.VideoId}/source/source.mp4",
                "replace-video-input", $"uploads/{workflow.VideoId}/source/source.mp4")
        };
        await harness.Store.CreateAsync(workflow, new UploadSession(workflow.VideoId, null,
            workflow.UploadExpiresAtUtc, workflow.MaximumSizeBytes, workflow.DeclaredSizeBytes), CancellationToken.None);
        return workflow;
    }

    private static string S3Message(VideoWorkflow workflow, string bucket, string key) => JsonSerializer.Serialize(new
    {
        Records = new[] { new { eventSource = "aws:s3", eventName = "ObjectCreated:Post",
            s3 = new { bucket = new { name = bucket }, @object = new { key } } } }
    });

    private static SnsEnvelope Envelope(string message, string? id = null) => new()
    {
        Type = "Notification",
        MessageId = id ?? Guid.NewGuid().ToString(),
        TopicArn = Topic,
        Message = message,
        Timestamp = DateTimeOffset.UtcNow,
        SignatureVersion = "2",
        Signature = "unused",
        SigningCertUrl = new Uri("https://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem")
    };

    private static async Task<VideoWorkflow> PrepareSubmittedAsync(SqliteHarness harness, string jobId = "job-1")
    {
        var workflow = await CreateS3WorkflowAsync(harness);
        var metadata = new SourceObjectMetadata(workflow.Source with { Identity = workflow.Source.Identity + Guid.NewGuid() },
            workflow.DeclaredSizeBytes, workflow.ContentType, null, null, new Dictionary<string, string>());
        await harness.Store.CompleteUploadAsync(workflow.VideoId, metadata, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            null, null, "test", CancellationToken.None);
        await harness.Store.TryClaimForDispatchAsync(workflow.VideoId, "worker", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        await harness.Store.MarkValidatingAsync(workflow.VideoId, CancellationToken.None);
        await harness.Store.RecordProviderStartedAsync(workflow.VideoId, jobId, TranscodeJobStatus.Submitted,
            DateTimeOffset.UtcNow, CancellationToken.None);
        return (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))!;
    }

    private static SnsEnvelope MediaConvertEnvelope(string videoId, string jobId, string status, string id)
    {
        var message = JsonSerializer.Serialize(new
        {
            source = "aws.mediaconvert",
            time = DateTimeOffset.UtcNow,
            detail = new { jobId, status, userMetadata = new { videoId } }
        });
        return new SnsEnvelope
        {
            Type = "Notification",
            MessageId = id,
            TopicArn = "arn:aws:sns:eu-west-1:111122223333:mediaconvert",
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
            SignatureVersion = "2",
            Signature = "unused",
            SigningCertUrl = new Uri("https://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem")
        };
    }
}
