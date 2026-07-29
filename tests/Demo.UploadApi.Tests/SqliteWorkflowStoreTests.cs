using System.Security.Cryptography;
using System.Text;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Persistence;
using Microsoft.Data.Sqlite;

namespace Demo.UploadApi.Tests;

public sealed class SqliteWorkflowStoreTests
{
    [Fact]
    public async Task Schema_create_and_read_round_trip()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = SqliteHarness.Workflow();
        await harness.Store.CreateAsync(workflow,
            new UploadSession(workflow.VideoId, new byte[32], workflow.UploadExpiresAtUtc,
                workflow.MaximumSizeBytes, workflow.DeclaredSizeBytes), CancellationToken.None);
        var read = await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None);
        Assert.Equal(workflow.VideoId, read?.VideoId);
        Assert.Equal(TranscodeJobStatus.UploadPending, read?.Status);
    }

    [Fact]
    public async Task Notification_and_source_completion_are_idempotent()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await CreatePendingAsync(harness);
        var metadata = Metadata(workflow);
        var first = await harness.Store.CompleteUploadAsync(workflow.VideoId, metadata, Hash(metadata.Source.Identity),
            DateTimeOffset.UtcNow, "topic", "message", "S3ObjectCreated", CancellationToken.None);
        var duplicateMessage = await harness.Store.CompleteUploadAsync(workflow.VideoId, metadata, Hash(metadata.Source.Identity),
            DateTimeOffset.UtcNow, "topic", "message", "S3ObjectCreated", CancellationToken.None);
        Assert.Equal(CompletionResult.Applied, first);
        Assert.Equal(CompletionResult.Duplicate, duplicateMessage);

        var other = await CreatePendingAsync(harness);
        var duplicateSource = await harness.Store.CompleteUploadAsync(other.VideoId,
            metadata with { Source = metadata.Source with { Provider = other.UploadProvider } }, Hash(metadata.Source.Identity),
            DateTimeOffset.UtcNow, "topic", "message-2", "S3ObjectCreated", CancellationToken.None);
        Assert.Equal(CompletionResult.Duplicate, duplicateSource);
    }

    [Fact]
    public async Task Two_dispatch_claims_race_and_only_one_wins()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await CreateUploadedAsync(harness);
        var tasks = new[]
        {
            harness.Store.TryClaimForDispatchAsync(workflow.VideoId, "one", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None),
            harness.Store.TryClaimForDispatchAsync(workflow.VideoId, "two", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None)
        };
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, result => result is not null);
        Assert.Equal(TranscodeJobStatus.Queued, results.Single(result => result is not null)!.Status);
    }

    [Fact]
    public async Task Expired_claim_is_recovered_to_uploaded()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await CreateUploadedAsync(harness);
        await harness.Store.TryClaimForDispatchAsync(workflow.VideoId, "old", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        Assert.Equal(1, await harness.Store.RecoverStaleClaimsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(TranscodeJobStatus.Uploaded, (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task Terminal_status_never_regresses_and_artifacts_are_persisted()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var workflow = await CreateUploadedAsync(harness);
        await harness.Store.TryClaimForDispatchAsync(workflow.VideoId, "worker", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        await harness.Store.MarkValidatingAsync(workflow.VideoId, CancellationToken.None);
        await harness.Store.RecordProviderStartedAsync(workflow.VideoId, "job-1", TranscodeJobStatus.Submitted,
            DateTimeOffset.UtcNow, CancellationToken.None);
        var artifact = new OutputArtifact(Guid.NewGuid().ToString(), workflow.VideoId, "Mp4", "video.mp4",
            $"outputs/{workflow.VideoId}/file/video.mp4", "video/mp4", 12, DateTimeOffset.UtcNow);
        await harness.Store.UpdateProviderStatusAsync(workflow.VideoId, "job-1", TranscodeJobStatus.Completed, 100,
            null, null, DateTimeOffset.UtcNow, [artifact], CancellationToken.None);
        await harness.Store.UpdateProviderStatusAsync(workflow.VideoId, "job-1", TranscodeJobStatus.Transcoding, 50,
            null, null, DateTimeOffset.UtcNow.AddMinutes(1), null, CancellationToken.None);
        Assert.Equal(TranscodeJobStatus.Completed, (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))?.Status);
        Assert.Single(await harness.Store.GetArtifactsAsync(workflow.VideoId, CancellationToken.None));
    }

    [Fact]
    public async Task External_provider_job_id_is_unique()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var first = await PrepareValidatingAsync(harness);
        var second = await PrepareValidatingAsync(harness);
        await harness.Store.RecordProviderStartedAsync(first.VideoId, "same-job", TranscodeJobStatus.Submitted,
            DateTimeOffset.UtcNow, CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() => harness.Store.RecordProviderStartedAsync(second.VideoId,
            "same-job", TranscodeJobStatus.Submitted, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    private static async Task<VideoWorkflow> CreatePendingAsync(SqliteHarness harness)
    {
        var workflow = SqliteHarness.Workflow();
        await harness.Store.CreateAsync(workflow, new UploadSession(workflow.VideoId, null,
            workflow.UploadExpiresAtUtc, workflow.MaximumSizeBytes, workflow.DeclaredSizeBytes), CancellationToken.None);
        return workflow;
    }

    private static async Task<VideoWorkflow> CreateUploadedAsync(SqliteHarness harness)
    {
        var workflow = await CreatePendingAsync(harness);
        var metadata = Metadata(workflow);
        await harness.Store.CompleteUploadAsync(workflow.VideoId, metadata, Hash(metadata.Source.Identity),
            DateTimeOffset.UtcNow, null, null, "test", CancellationToken.None);
        return (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))!;
    }

    private static async Task<VideoWorkflow> PrepareValidatingAsync(SqliteHarness harness)
    {
        var workflow = await CreateUploadedAsync(harness);
        await harness.Store.TryClaimForDispatchAsync(workflow.VideoId, "worker", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        await harness.Store.MarkValidatingAsync(workflow.VideoId, CancellationToken.None);
        return (await harness.Store.GetAsync(workflow.VideoId, CancellationToken.None))!;
    }

    private static SourceObjectMetadata Metadata(VideoWorkflow workflow)
    {
        var source = workflow.Source with { Identity = workflow.Source.Identity + ":complete" };
        return new SourceObjectMetadata(source, workflow.DeclaredSizeBytes, workflow.ContentType, "etag", "checksum",
            new Dictionary<string, string>());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
