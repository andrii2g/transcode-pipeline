using System.Security.Cryptography;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace Demo.UploadApi.Persistence;

public sealed partial class SqliteWorkflowStore
{
    public async Task<LocalUploadClaimResult> TryClaimLocalUploadAsync(
        string videoId, ReadOnlyMemory<byte> tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        byte[]? storedHash;
        string status;
        DateTimeOffset expiresAt;
        DateTimeOffset? completedAt;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT s.token_hash, s.completed_at_utc, w.status, w.upload_expires_at_utc
                FROM upload_sessions s JOIN video_workflows w ON w.video_id = s.video_id
                WHERE s.video_id = $videoId;
                """;
            Add(select, "$videoId", videoId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return LocalUploadClaimResult.NotFound;
            storedHash = reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0);
            completedAt = GetDate(reader, 1);
            status = reader.GetString(2);
            expiresAt = Parse(reader.GetString(3));
        }

        if (storedHash is null || !CryptographicOperations.FixedTimeEquals(storedHash, tokenHash.Span))
            return LocalUploadClaimResult.InvalidToken;
        if (completedAt is not null) return LocalUploadClaimResult.AlreadyUsed;
        if (status != TranscodeJobStatus.UploadPending.ToString()) return LocalUploadClaimResult.InvalidState;
        if (expiresAt <= now)
        {
            await SetUploadErrorAsync(connection, transaction, videoId, TranscodeJobStatus.Expired,
                "UploadExpired", "The upload session has expired.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return LocalUploadClaimResult.Expired;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE upload_sessions SET claimed_at_utc = $now
            WHERE video_id = $videoId AND claimed_at_utc IS NULL;
            UPDATE video_workflows
            SET status = 'Uploading', upload_started_at_utc = $now, row_version = row_version + 1
            WHERE video_id = $videoId AND status = 'UploadPending';
            """;
        Add(update, "$now", Format(now));
        Add(update, "$videoId", videoId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) < 2) return LocalUploadClaimResult.AlreadyUsed;
        await transaction.CommitAsync(cancellationToken);
        return LocalUploadClaimResult.Claimed;
    }

    public async Task<CompletionResult> CompleteUploadAsync(
        string videoId, SourceObjectMetadata metadata, string sourceIdentityHash,
        DateTimeOffset occurredAtUtc, string? topicArn, string? messageId, string notificationType,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (topicArn is not null && messageId is not null &&
            !await InsertNotificationAsync(connection, transaction, topicArn, messageId, notificationType,
                metadata.Source.Identity, occurredAtUtc, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return CompletionResult.Duplicate;
        }

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT video_id FROM video_workflows WHERE source_identity_hash = $identity LIMIT 1;";
            Add(duplicate, "$identity", sourceIdentityHash);
            if (await duplicate.ExecuteScalarAsync(cancellationToken) is string)
            {
                await transaction.CommitAsync(cancellationToken);
                return CompletionResult.Duplicate;
            }
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE video_workflows
            SET status = 'Uploaded', actual_size_bytes = $size, source_bucket = $bucket,
                source_key = $key, source_version_id = $versionId,
                source_local_relative_path = $localPath, source_etag = $etag,
                source_checksum_sha256 = $checksum, source_identity_hash = $identity,
                uploaded_at_utc = $uploadedAt, error_code = NULL, error_message = NULL,
                row_version = row_version + 1
            WHERE video_id = $videoId AND status IN ('UploadPending', 'Uploading');
            """;
        Add(update, "$size", metadata.SizeBytes);
        Add(update, "$bucket", metadata.Source.Bucket);
        Add(update, "$key", metadata.Source.Key);
        Add(update, "$versionId", metadata.Source.VersionId);
        Add(update, "$localPath", metadata.Source.LocalRelativePath);
        Add(update, "$etag", metadata.ETag);
        Add(update, "$checksum", metadata.ChecksumSha256);
        Add(update, "$identity", sourceIdentityHash);
        Add(update, "$uploadedAt", Format(occurredAtUtc));
        Add(update, "$videoId", videoId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT status FROM video_workflows WHERE video_id = $videoId;";
            Add(exists, "$videoId", videoId);
            var current = (string?)await exists.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return current is null ? CompletionResult.NotFound : CompletionResult.InvalidState;
        }

        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = "UPDATE upload_sessions SET completed_at_utc = $now WHERE video_id = $videoId;";
            Add(session, "$now", Format(occurredAtUtc));
            Add(session, "$videoId", videoId);
            await session.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return CompletionResult.Applied;
    }

    public async Task<bool> RecordNotificationAsync(
        string topicArn, string messageId, string notificationType, string? sourceIdentity,
        DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await InsertNotificationAsync(connection, null, topicArn, messageId, notificationType,
            sourceIdentity, receivedAtUtc, cancellationToken);
    }

    public async Task RejectUploadAsync(string videoId, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await SetUploadErrorAsync(connection, null, videoId, TranscodeJobStatus.UploadRejected,
            errorCode, errorMessage, cancellationToken);
    }

    private static async Task<bool> InsertNotificationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string topicArn, string messageId,
        string notificationType, string? sourceIdentity, DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processed_notifications(topic_arn, message_id, notification_type, source_identity, received_at_utc)
            VALUES ($topicArn, $messageId, $type, $sourceIdentity, $receivedAt)
            ON CONFLICT(topic_arn, message_id) DO NOTHING;
            """;
        Add(command, "$topicArn", topicArn);
        Add(command, "$messageId", messageId);
        Add(command, "$type", notificationType);
        Add(command, "$sourceIdentity", sourceIdentity);
        Add(command, "$receivedAt", Format(receivedAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task SetUploadErrorAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string videoId,
        TranscodeJobStatus status, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE video_workflows SET status = $status, error_code = $errorCode,
                error_message = $errorMessage, row_version = row_version + 1
            WHERE video_id = $videoId AND status IN ('UploadPending', 'Uploading');
            """;
        Add(command, "$status", status.ToString());
        Add(command, "$errorCode", errorCode);
        Add(command, "$errorMessage", errorMessage);
        Add(command, "$videoId", videoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
