using System.Security.Cryptography;
using System.Text;
using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Application;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Infrastructure.Aws;

public sealed class MediaConvertTranscodeProvider(
    IAmazonMediaConvert mediaConvert,
    IOptions<MediaConvertOptions> options,
    IOptions<S3StorageOptions> storageOptions) : ITranscodeProvider
{
    private readonly MediaConvertOptions _options = options.Value;
    private readonly S3StorageOptions _storage = storageOptions.Value;
    public TranscodeProviderKind Kind => TranscodeProviderKind.MediaConvert;

    public async Task<TranscodeStartResult> StartAsync(VideoWorkflow workflow, CancellationToken cancellationToken)
    {
        var outputBase = $"s3://{_storage.OutputBucket}/{_storage.OutputPrefix.Trim('/')}/{workflow.VideoId}/";
        var request = new CreateJobRequest
        {
            Role = _options.RoleArn,
            Queue = string.IsNullOrWhiteSpace(_options.QueueArn) ? null : _options.QueueArn,
            JobTemplate = _options.JobTemplateName,
            ClientRequestToken = StableToken(workflow),
            UserMetadata = new Dictionary<string, string>
            {
                ["videoId"] = workflow.VideoId,
                ["workflowName"] = _options.WorkflowName,
                ["profile"] = workflow.ProfileName
            },
            Settings = new JobSettings
            {
                Inputs = [new Input { FileInput = $"s3://{workflow.Source.Bucket}/{workflow.Source.Key}" }],
                OutputGroups =
                [
                    new OutputGroup
                    {
                        Name = "HLS",
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.HLS_GROUP_SETTINGS,
                            HlsGroupSettings = new HlsGroupSettings { Destination = outputBase + "hls/" }
                        }
                    },
                    new OutputGroup
                    {
                        Name = "File",
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.FILE_GROUP_SETTINGS,
                            FileGroupSettings = new FileGroupSettings { Destination = outputBase + "file/" }
                        }
                    }
                ]
            }
        };
        var response = await mediaConvert.CreateJobAsync(request, cancellationToken);
        return new TranscodeStartResult(response.Job.Id, TranscodeJobStatus.Submitted);
    }

    public static string StableToken(VideoWorkflow workflow)
    {
        var input = $"{workflow.VideoId}|{workflow.ProfileName}|1";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
