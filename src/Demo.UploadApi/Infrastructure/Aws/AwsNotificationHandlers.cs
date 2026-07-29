using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Application;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Infrastructure.Aws;

public sealed class S3SnsNotificationHandler(
    IWorkflowStore store,
    IUploadProvider uploadProvider,
    IUploadCompletedNotificationPublisher publisher,
    IOptions<S3StorageOptions> options,
    TimeProvider timeProvider)
{
    private readonly S3StorageOptions _options = options.Value;

    public async Task HandleAsync(SnsEnvelope envelope, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(envelope.Message);
        var root = document.RootElement;
        if (root.TryGetProperty("Event", out var eventName) &&
            eventName.GetString() == "s3:TestEvent")
        {
            await store.RecordNotificationAsync(envelope.TopicArn, envelope.MessageId, "S3TestEvent", null,
                timeProvider.GetUtcNow(), cancellationToken);
            return;
        }
        if (!root.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SNS Message does not contain an S3 event.");

        var index = 0;
        foreach (var record in records.EnumerateArray())
        {
            var source = record.GetProperty("eventSource").GetString();
            var name = record.GetProperty("eventName").GetString();
            if (source != "aws:s3" || name is null || !name.StartsWith("ObjectCreated:", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected S3 event source or event name.");
            var bucket = record.GetProperty("s3").GetProperty("bucket").GetProperty("name").GetString();
            var encodedKey = record.GetProperty("s3").GetProperty("object").GetProperty("key").GetString();
            var key = WebUtility.UrlDecode(encodedKey ?? string.Empty);
            if (!string.Equals(bucket, _options.InputBucket, StringComparison.Ordinal) ||
                !key.StartsWith(_options.UploadPrefix.Trim('/') + "/", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("S3 notification bucket or prefix is not allowed.");
            var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4 || !Guid.TryParse(segments[1], out _))
                throw new InvalidDataException("S3 object key is not a canonical workflow source key.");
            var videoId = segments[1];
            var workflow = await store.GetAsync(videoId, cancellationToken);
            if (workflow is null) throw new InvalidDataException("S3 notification references an unknown workflow.");
            if (workflow.UploadProvider != UploadProviderKind.S3PresignedPost ||
                !string.Equals(workflow.Source.Bucket, bucket, StringComparison.Ordinal) ||
                !string.Equals(workflow.Source.Key, key, StringComparison.Ordinal))
            {
                await RejectAsync(workflow.VideoId, envelope, "SourceIdentityMismatch",
                    "The uploaded S3 object does not match its workflow source.", cancellationToken);
                index++;
                continue;
            }

            var metadata = await uploadProvider.InspectAsync(workflow, cancellationToken);
            var metadataVideoId = FindMetadata(metadata.Metadata, "x-amz-meta-video-id", "video-id");
            if (!string.Equals(metadataVideoId, videoId, StringComparison.Ordinal) ||
                metadata.SizeBytes > workflow.MaximumSizeBytes ||
                (_options.RequireDeclaredSizeMatch && metadata.SizeBytes != workflow.DeclaredSizeBytes))
            {
                var code = metadata.SizeBytes > workflow.MaximumSizeBytes ? "FileSizeExceeded" : "SourceMetadataMismatch";
                await RejectAsync(videoId, envelope, code, "S3 source metadata or size did not match the upload session.", cancellationToken);
                index++;
                continue;
            }
            var identityHash = Sha256(metadata.Source.Identity);
            var messageId = index == 0 ? envelope.MessageId : $"{envelope.MessageId}:{index}";
            var completion = await store.CompleteUploadAsync(videoId, metadata, identityHash,
                envelope.Timestamp, envelope.TopicArn, messageId, "S3ObjectCreated", cancellationToken);
            if (completion == CompletionResult.Applied)
                await publisher.PublishAsync(new UploadCompletedNotification(envelope.MessageId, videoId,
                    UploadProviderKind.S3PresignedPost, metadata.Source, envelope.Timestamp), cancellationToken);
            index++;
        }
    }

    private async Task RejectAsync(string videoId, SnsEnvelope envelope, string code, string message,
        CancellationToken cancellationToken)
    {
        await store.RecordNotificationAsync(envelope.TopicArn, envelope.MessageId, "S3ObjectRejected", videoId,
            timeProvider.GetUtcNow(), cancellationToken);
        await store.RejectUploadAsync(videoId, code, message, cancellationToken);
    }

    private static string? FindMetadata(IReadOnlyDictionary<string, string> values, params string[] names)
    {
        foreach (var name in names) if (values.TryGetValue(name, out var value)) return value;
        return null;
    }
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class MediaConvertSnsNotificationHandler(
    IWorkflowStore store,
    IOptions<S3StorageOptions> storageOptions,
    TimeProvider timeProvider)
{
    private readonly S3StorageOptions _storage = storageOptions.Value;

    public async Task HandleAsync(SnsEnvelope envelope, CancellationToken cancellationToken)
    {

        using var document = JsonDocument.Parse(envelope.Message);
        var root = document.RootElement;
        if (root.GetProperty("source").GetString() != "aws.mediaconvert")
            throw new InvalidDataException("Unexpected EventBridge source.");
        var detail = root.GetProperty("detail");
        var jobId = detail.GetProperty("jobId").GetString() ?? throw new InvalidDataException("MediaConvert jobId is missing.");
        var providerStatus = detail.GetProperty("status").GetString();
        var metadata = detail.GetProperty("userMetadata");
        var videoId = metadata.GetProperty("videoId").GetString() ?? throw new InvalidDataException("MediaConvert videoId metadata is missing.");
        var workflow = await store.GetAsync(videoId, cancellationToken) ?? throw new InvalidDataException("MediaConvert event references an unknown workflow.");
        if (!string.Equals(workflow.ExternalJobId, jobId, StringComparison.Ordinal))
            throw new InvalidDataException("MediaConvert event jobId does not match the workflow.");
        var status = MapStatus(providerStatus);
        var progress = TryGetProgress(detail);
        var errorCode = detail.TryGetProperty("errorCode", out var code) ? code.ToString() : null;
        var errorMessage = detail.TryGetProperty("errorMessage", out var message) ? message.GetString() : null;
        var occurredAt = root.TryGetProperty("time", out var time) && time.TryGetDateTimeOffset(out var parsed)
            ? parsed : envelope.Timestamp;
        IReadOnlyList<OutputArtifact>? artifacts = status == TranscodeJobStatus.Completed
            ? BuildArtifacts(videoId, occurredAt) : null;
        await store.UpdateProviderStatusAsync(videoId, jobId, status, progress, errorCode, errorMessage,
            occurredAt, artifacts, cancellationToken);
        await store.RecordNotificationAsync(envelope.TopicArn, envelope.MessageId,
            "MediaConvertEvent", videoId, timeProvider.GetUtcNow(), cancellationToken);
    }

    private IReadOnlyList<OutputArtifact> BuildArtifacts(string videoId, DateTimeOffset now)
    {
        var prefix = $"{_storage.OutputPrefix.Trim('/')}/{videoId}";
        return
        [
            new(Guid.CreateVersion7().ToString(), videoId, "HlsMasterPlaylist", "master.m3u8",
                $"{prefix}/hls/master.m3u8", "application/vnd.apple.mpegurl", null, now),
            new(Guid.CreateVersion7().ToString(), videoId, "Mp4", "video.mp4",
                $"{prefix}/file/video.mp4", "video/mp4", null, now)
        ];
    }

    private static TranscodeJobStatus MapStatus(string? status) => status switch
    {
        "INPUT_INFORMATION" or "PROGRESSING" or "STATUS_UPDATE" => TranscodeJobStatus.Transcoding,
        "COMPLETE" => TranscodeJobStatus.Completed,
        "ERROR" => TranscodeJobStatus.Failed,
        "CANCELED" => TranscodeJobStatus.Canceled,
        _ => throw new InvalidDataException("Unsupported MediaConvert event status.")
    };

    private static double? TryGetProgress(JsonElement detail)
    {
        if (detail.TryGetProperty("jobProgress", out var progress) &&
            progress.TryGetProperty("jobPercentComplete", out var percent) && percent.TryGetDouble(out var value)) return value;
        return null;
    }
}

public sealed class AwsNotificationService(
    ISnsMessageSignatureVerifier verifier,
    ISnsSubscriptionConfirmationService confirmation,
    S3SnsNotificationHandler s3Handler,
    MediaConvertSnsNotificationHandler mediaConvertHandler,
    IWorkflowStore store,
    IOptions<AwsNotificationOptions> options,
    TimeProvider timeProvider)
{
    private readonly AwsNotificationOptions _options = options.Value;

    public Task HandleUploadAsync(SnsEnvelope envelope, CancellationToken cancellationToken) =>
        HandleAsync(envelope, _options.UploadTopicArn, s3Handler.HandleAsync, cancellationToken);
    public Task HandleMediaConvertAsync(SnsEnvelope envelope, CancellationToken cancellationToken) =>
        HandleAsync(envelope, _options.MediaConvertTopicArn, mediaConvertHandler.HandleAsync, cancellationToken);

    private async Task HandleAsync(SnsEnvelope envelope, string expectedTopic,
        Func<SnsEnvelope, CancellationToken, Task> notificationHandler, CancellationToken cancellationToken)
    {
        await verifier.VerifyAsync(envelope, expectedTopic, cancellationToken);
        switch (envelope.Type)
        {
            case "SubscriptionConfirmation":
                await confirmation.ConfirmAsync(envelope, cancellationToken);
                await store.RecordNotificationAsync(envelope.TopicArn, envelope.MessageId, envelope.Type,
                    null, timeProvider.GetUtcNow(), cancellationToken);
                break;
            case "Notification": await notificationHandler(envelope, cancellationToken); break;
            case "UnsubscribeConfirmation":
                await store.RecordNotificationAsync(envelope.TopicArn, envelope.MessageId, envelope.Type,
                    null, timeProvider.GetUtcNow(), cancellationToken);
                break;
            default: throw new InvalidDataException("Unsupported SNS message type.");
        }
    }
}
