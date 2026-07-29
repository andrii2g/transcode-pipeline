using System.Security.Cryptography;
using System.Text;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Infrastructure.Uploads;
using Demo.UploadApi.Options;
using Demo.UploadApi.Persistence;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Application;

public interface ILocalUploadService
{
    Task UploadAsync(string videoId, string token, Stream content, long? contentLength,
        string? contentType, CancellationToken cancellationToken);
}

public sealed class LocalUploadService : ILocalUploadService, IDisposable
{
    private readonly IWorkflowStore _store;
    private readonly IUploadCompletedNotificationPublisher _publisher;
    private readonly LocalPathResolver _paths;
    private readonly LocalStorageOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _capacity;

    public LocalUploadService(
        IWorkflowStore store,
        IUploadCompletedNotificationPublisher publisher,
        LocalPathResolver paths,
        IOptions<LocalStorageOptions> options,
        TimeProvider timeProvider)
    {
        _store = store;
        _publisher = publisher;
        _paths = paths;
        _options = options.Value;
        _timeProvider = timeProvider;
        _capacity = new SemaphoreSlim(_options.MaximumConcurrentUploads, _options.MaximumConcurrentUploads);
    }

    public async Task UploadAsync(
        string videoId, string token, Stream content, long? contentLength, string? contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new LocalUploadException("UploadTokenInvalid", "A valid upload token is required.", 401);
        if (!await _capacity.WaitAsync(TimeSpan.Zero, cancellationToken))
            throw new LocalUploadException("UploadCapacityUnavailable", "Upload capacity is temporarily unavailable.", 503);

        string? temporaryPath = null;
        var published = false;
        var claimed = false;
        try
        {
            var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var now = _timeProvider.GetUtcNow();
            var claim = await _store.TryClaimLocalUploadAsync(videoId, tokenHash, now, cancellationToken);
            ThrowForClaim(claim);
            claimed = true;
            var workflow = await _store.GetAsync(videoId, cancellationToken)
                ?? throw new LocalUploadException("UploadNotFound", "The upload session was not found.", 404);
            if (workflow.UploadProvider != UploadProviderKind.LocalHttp)
                throw new LocalUploadException("UploadProviderMismatch", "This upload is not a local HTTP session.", 409);
            if (workflow.ContentType is not null && contentType is not null &&
                !string.Equals(workflow.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
                throw new LocalUploadException("UnsupportedMediaType", "Content-Type does not match the upload session.", 415);
            if (contentLength is > 0 && contentLength > workflow.MaximumSizeBytes)
                throw new LocalUploadException("FileSizeExceeded", "The upload exceeds its session limit.", 413);

            temporaryPath = _paths.TemporaryUploadPath(videoId);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > workflow.MaximumSizeBytes)
                        throw new LocalUploadException("FileSizeExceeded", "The upload exceeds its session limit.", 413);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            if (_options.RequireDeclaredSizeMatch && total != workflow.DeclaredSizeBytes)
                throw new LocalUploadException("DeclaredSizeMismatch", "Uploaded bytes do not match sizeBytes.", 422);
            if (total == 0) throw new LocalUploadException("DeclaredSizeMismatch", "The uploaded body was empty.", 422);

            var checksum = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var finalPath = _paths.SourcePath(workflow.Source.LocalRelativePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(temporaryPath, finalPath, overwrite: false);
            published = true;
            var source = workflow.Source with { Identity = $"local:{workflow.Source.LocalRelativePath}:{checksum}" };
            var metadata = new SourceObjectMetadata(source, total, workflow.ContentType, null, checksum,
                new Dictionary<string, string>());
            var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Identity))).ToLowerInvariant();
            var result = await _store.CompleteUploadAsync(videoId, metadata, identityHash, now,
                null, null, "LocalUploadCompleted", cancellationToken);
            if (result != CompletionResult.Applied)
                throw new LocalUploadException("UploadAlreadyUsed", "The upload session was already completed.", 409);
            await _publisher.PublishAsync(new UploadCompletedNotification(Guid.CreateVersion7().ToString(), videoId,
                UploadProviderKind.LocalHttp, source, now), cancellationToken);
        }
        catch (LocalUploadException exception)
        {
            if (claimed && !published) await _store.RejectUploadAsync(videoId, exception.Code, exception.Message, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (claimed && !published)
        {
            await _store.RejectUploadAsync(videoId, "UploadCanceled", "The upload was interrupted before publication.", CancellationToken.None);
            throw;
        }
        finally
        {
            if (!published && temporaryPath is not null) File.Delete(temporaryPath);
            _capacity.Release();
        }
    }

    private static void ThrowForClaim(LocalUploadClaimResult result)
    {
        switch (result)
        {
            case LocalUploadClaimResult.Claimed: return;
            case LocalUploadClaimResult.NotFound: throw new LocalUploadException("UploadNotFound", "The upload session was not found.", 404);
            case LocalUploadClaimResult.InvalidToken: throw new LocalUploadException("UploadTokenInvalid", "The upload token is invalid.", 401);
            case LocalUploadClaimResult.Expired: throw new LocalUploadException("UploadExpired", "The upload session has expired.", 410);
            case LocalUploadClaimResult.AlreadyUsed: throw new LocalUploadException("UploadAlreadyUsed", "The upload token has already been used.", 409);
            default: throw new LocalUploadException("UploadAlreadyUsed", "The upload session is not writable.", 409);
        }
    }

    public void Dispose() => _capacity.Dispose();
}
