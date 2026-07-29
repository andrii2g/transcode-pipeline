using System.Security.Cryptography;
using System.Text;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Application;

public sealed class LocalUploadRecoveryWorker(
    IWorkflowStore store, IUploadProvider uploadProvider,
    IUploadCompletedNotificationPublisher notifications,
    IOptions<TranscodeDispatcherOptions> options, TimeProvider timeProvider,
    ILogger<LocalUploadRecoveryWorker> logger) : BackgroundService
{
    private readonly TranscodeDispatcherOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var workflow in await store.ListUploadingAsync(_options.MaximumConcurrentJobs * 2, stoppingToken))
            {
                try
                {
                    var metadata = await uploadProvider.InspectAsync(workflow, stoppingToken);
                    if (metadata.SizeBytes > workflow.MaximumSizeBytes || metadata.SizeBytes != workflow.DeclaredSizeBytes)
                    {
                        await store.RejectUploadAsync(workflow.VideoId, "RecoveredUploadSizeMismatch",
                            "Recovered local source size did not match its upload session.", stoppingToken);
                        continue;
                    }
                    var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata.Source.Identity))).ToLowerInvariant();
                    if (await store.CompleteUploadAsync(workflow.VideoId, metadata, identityHash, timeProvider.GetUtcNow(),
                        null, null, "RecoveredLocalPublication", stoppingToken) == CompletionResult.Applied)
                    {
                        await notifications.PublishAsync(new UploadCompletedNotification(Guid.CreateVersion7().ToString(),
                            workflow.VideoId, UploadProviderKind.LocalHttp, metadata.Source, timeProvider.GetUtcNow()), stoppingToken);
                        logger.LogInformation("Recovered atomically published local upload {VideoId}.", workflow.VideoId);
                    }
                }
                catch (FileNotFoundException) { }
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.ScanIntervalSeconds), stoppingToken);
        }
    }
}
