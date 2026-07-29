using System.Collections.Concurrent;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Application;

public sealed class TranscodeDispatchWorker(
    IWorkflowStore store,
    IUploadCompletedNotificationPublisher notifications,
    ITranscodeProvider provider,
    IOptions<TranscodeDispatcherOptions> options,
    TimeProvider timeProvider,
    ILogger<TranscodeDispatchWorker> logger) : BackgroundService
{
    private readonly TranscodeDispatcherOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, Task> _active = new();
    private readonly SemaphoreSlim _capacity = new(options.Value.MaximumConcurrentJobs, options.Value.MaximumConcurrentJobs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await store.RecoverStaleClaimsAsync(timeProvider.GetUtcNow(), stoppingToken);
        await using var enumerator = notifications.ReadAllAsync(stoppingToken).GetAsyncEnumerator(stoppingToken);
        Task<bool>? pendingSignal = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchPersistentWorkAsync(stoppingToken);
            pendingSignal ??= enumerator.MoveNextAsync().AsTask();
            var delay = Task.Delay(TimeSpan.FromSeconds(_options.ScanIntervalSeconds), stoppingToken);
            if (await Task.WhenAny(pendingSignal, delay) == pendingSignal) pendingSignal = null;
            await store.RecoverStaleClaimsAsync(timeProvider.GetUtcNow(), stoppingToken);
        }
    }

    private async Task DispatchPersistentWorkAsync(CancellationToken cancellationToken)
    {
        var workflows = await store.ListDispatchableAsync(_options.MaximumConcurrentJobs * 2, cancellationToken);
        foreach (var workflow in workflows)
        {
            if (_active.ContainsKey(workflow.VideoId)) continue;
            await _capacity.WaitAsync(cancellationToken);
            var task = ProcessAsync(workflow.VideoId, cancellationToken);
            _active[workflow.VideoId] = task;
            _ = task.ContinueWith(completedTask =>
            {
                _active.TryRemove(workflow.VideoId, out var removedTask);
                _capacity.Release();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ProcessAsync(string videoId, CancellationToken cancellationToken)
    {
        var instanceId = string.IsNullOrWhiteSpace(_options.InstanceId)
            ? $"{Environment.MachineName}:{Environment.ProcessId}" : _options.InstanceId;
        var claimed = await store.TryClaimForDispatchAsync(videoId, instanceId!,
            timeProvider.GetUtcNow().AddMinutes(_options.ClaimTimeoutMinutes), cancellationToken);
        if (claimed is null) return;
        try
        {
            await store.MarkValidatingAsync(videoId, cancellationToken);
            var validating = await store.GetAsync(videoId, cancellationToken) ?? claimed;
            var result = await provider.StartAsync(validating, cancellationToken);
            if (result.Status is TranscodeJobStatus.Submitted or TranscodeJobStatus.Transcoding)
                await store.RecordProviderStartedAsync(videoId, result.ExternalJobId, result.Status,
                    timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Transcode dispatch failed for {VideoId} using {Provider}.", videoId, provider.Kind);
            await store.UpdateProviderStatusAsync(videoId, null, TranscodeJobStatus.Failed, null,
                "DispatchFailed", exception.Message, timeProvider.GetUtcNow(), null, CancellationToken.None);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await Task.WhenAll(_active.Values).WaitAsync(cancellationToken);
        _capacity.Dispose();
    }
}

public interface ICompatibilityTranscodeService
{
    Task<VideoWorkflow?> TriggerAsync(string videoId, CancellationToken cancellationToken);
}

public sealed class CompatibilityTranscodeService(
    IWorkflowStore store,
    IUploadProvider uploadProvider,
    IUploadCompletedNotificationPublisher publisher,
    TimeProvider timeProvider) : ICompatibilityTranscodeService
{
    public async Task<VideoWorkflow?> TriggerAsync(string videoId, CancellationToken cancellationToken)
    {
        var workflow = await store.GetAsync(videoId, cancellationToken);
        if (workflow is null || workflow.Status != TranscodeJobStatus.UploadPending) return workflow;
        var metadata = await uploadProvider.InspectAsync(workflow, cancellationToken);
        if (metadata.SizeBytes > workflow.MaximumSizeBytes)
        {
            await store.RejectUploadAsync(videoId, "FileSizeExceeded", "The source exceeds its upload session limit.", cancellationToken);
            return await store.GetAsync(videoId, cancellationToken);
        }
        var identityHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(metadata.Source.Identity))).ToLowerInvariant();
        if (await store.CompleteUploadAsync(videoId, metadata, identityHash, timeProvider.GetUtcNow(),
            null, null, "CompatibilityCompletion", cancellationToken) == CompletionResult.Applied)
            await publisher.PublishAsync(new UploadCompletedNotification(Guid.CreateVersion7().ToString(), videoId,
                workflow.UploadProvider, metadata.Source, timeProvider.GetUtcNow()), cancellationToken);
        return await store.GetAsync(videoId, cancellationToken);
    }
}
