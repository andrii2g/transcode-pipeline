using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Demo.Contracts.Enums;
using Demo.UploadApi.Infrastructure.Uploads;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Tests;

public sealed class S3PresignedPostTests
{
    [Fact]
    public async Task Policy_has_exact_key_metadata_content_type_encryption_and_size_range()
    {
        using var client = new AmazonS3Client(new BasicAWSCredentials("access-key", "secret-key"),
            new AmazonS3Config { RegionEndpoint = RegionEndpoint.EUWest1 });
        var options = Microsoft.Extensions.Options.Options.Create(new S3StorageOptions
        {
            Region = "eu-west-1",
            InputBucket = "private-input",
            OutputBucket = "private-output",
            UploadPrefix = "uploads",
            PresignedPostExpirationMinutes = 15,
            ServerSideEncryption = "AES256"
        });
        var provider = new S3PresignedPostUploadProvider(client, options, TimeProvider.System);
        var workflow = SqliteHarness.Workflow(upload: UploadProviderKind.S3PresignedPost,
            transcode: TranscodeProviderKind.MediaConvert, declared: 100, maximum: 20L * 1024 * 1024);
        workflow = workflow with
        {
            Source = workflow.Source with
            {
                Bucket = "private-input",
                Key = $"uploads/{workflow.VideoId}/source/source.mp4",
                Identity = $"s3://private-input/uploads/{workflow.VideoId}/source/source.mp4"
            }
        };

        var instruction = await provider.CreateInstructionAsync(workflow, null, CancellationToken.None);

        Assert.Equal("POST", instruction.Method);
        Assert.Equal(workflow.Source.Key, instruction.FormFields["key"]);
        Assert.Equal(workflow.VideoId, instruction.FormFields["x-amz-meta-video-id"]);
        Assert.Equal("video/mp4", instruction.FormFields["Content-Type"]);
        Assert.Equal("AES256", instruction.FormFields["x-amz-server-side-encryption"]);
        Assert.Contains("policy", instruction.FormFields.Keys);
        Assert.Contains(instruction.FormFields.Keys, key => key.Contains("signature", StringComparison.OrdinalIgnoreCase));

        var policyBytes = Convert.FromBase64String(instruction.FormFields["policy"]);
        using var policy = JsonDocument.Parse(policyBytes);
        var json = Encoding.UTF8.GetString(policyBytes);
        Assert.Contains(workflow.Source.Key!, json, StringComparison.Ordinal);
        Assert.Contains("content-length-range", json, StringComparison.Ordinal);
        Assert.Contains(workflow.MaximumSizeBytes.ToString(), json, StringComparison.Ordinal);
        Assert.Contains(workflow.VideoId, json, StringComparison.Ordinal);
        var expiration = policy.RootElement.GetProperty("expiration").GetDateTimeOffset();
        Assert.InRange(expiration, DateTimeOffset.UtcNow.AddMinutes(13), DateTimeOffset.UtcNow.AddMinutes(16));
    }
}
