using Amazon.S3;
using Amazon.S3.Model;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Application;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Infrastructure.Uploads;

public sealed class S3PresignedPostUploadProvider(
    IAmazonS3 s3,
    IOptions<S3StorageOptions> options,
    TimeProvider timeProvider) : IUploadProvider
{
    private readonly S3StorageOptions _options = options.Value;
    public UploadProviderKind Kind => UploadProviderKind.S3PresignedPost;

    public async Task<UploadInstruction> CreateInstructionAsync(
        VideoWorkflow workflow, string? oneTimeToken, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = workflow.Source.Key!,
            ["x-amz-meta-video-id"] = workflow.VideoId,
            ["success_action_status"] = "204"
        };
        var conditions = new List<S3PostCondition>
        {
            S3PostCondition.ExactMatch("key", workflow.Source.Key!),
            S3PostCondition.ExactMatch("x-amz-meta-video-id", workflow.VideoId),
            S3PostCondition.ExactMatch("success_action_status", "204"),
            S3PostCondition.ContentLengthRange(1, workflow.MaximumSizeBytes)
        };
        if (workflow.ContentType is not null)
        {
            fields["Content-Type"] = workflow.ContentType;
            conditions.Add(S3PostCondition.ExactMatch("Content-Type", workflow.ContentType));
        }
        if (!string.IsNullOrWhiteSpace(_options.ServerSideEncryption))
        {
            fields["x-amz-server-side-encryption"] = _options.ServerSideEncryption;
            conditions.Add(S3PostCondition.ExactMatch("x-amz-server-side-encryption", _options.ServerSideEncryption));
        }
        var request = new CreatePresignedPostRequest
        {
            BucketName = _options.InputBucket,
            Key = workflow.Source.Key,
            Expires = timeProvider.GetUtcNow().AddMinutes(_options.PresignedPostExpirationMinutes).UtcDateTime,
            Fields = fields,
            Conditions = conditions
        };
        var response = await s3.CreatePresignedPostAsync(request);
        var responseFields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in response.Fields)
        {
            var name = pair.Key.Equals("Policy", StringComparison.OrdinalIgnoreCase) ? "policy" :
                pair.Key.StartsWith("X-Amz-", StringComparison.OrdinalIgnoreCase) ? pair.Key.ToLowerInvariant() : pair.Key;
            responseFields[name] = pair.Value;
        }
        return new UploadInstruction(Kind, "POST", new Uri(response.Url),
            new Dictionary<string, string>(), responseFields);
    }

    public async Task<SourceObjectMetadata> InspectAsync(VideoWorkflow workflow, CancellationToken cancellationToken)
    {
        var response = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = workflow.Source.Bucket,
            Key = workflow.Source.Key,
            VersionId = workflow.Source.VersionId
        }, cancellationToken);
        var metadata = response.Metadata.Keys.ToDictionary(
            key => key, key => response.Metadata[key], StringComparer.OrdinalIgnoreCase);
        var source = workflow.Source with
        {
            VersionId = response.VersionId,
            Identity = $"s3://{workflow.Source.Bucket}/{workflow.Source.Key}#{response.VersionId ?? response.ETag ?? string.Empty}"
        };
        return new SourceObjectMetadata(source, response.ContentLength, response.Headers.ContentType,
            response.ETag, response.ChecksumSHA256, metadata);
    }
}
