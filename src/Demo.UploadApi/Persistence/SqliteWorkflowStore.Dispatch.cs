using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace Demo.UploadApi.Persistence;

public sealed partial class SqliteWorkflowStore
{
    public async Task<IReadOnlyList<VideoWorkflow>> ListUploadingAsync(int maximum, CancellationToken cancellationToken)
    {
        var workflows = new List<VideoWorkflow>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {WorkflowColumns} FROM video_workflows WHERE status = 'Uploading' ORDER BY created_at_utc LIMIT $maximum;";
        Add(command, "$maximum", maximum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) workflows.Add(MapWorkflow(reader));
        return workflows;
    }

    public async Task<IReadOnlyList<VideoWorkflow>> ListDispatchableAsync(int maximum, CancellationToken cancellationToken)
    {
        var workflows = new List<VideoWorkflow>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {WorkflowColumns} FROM video_workflows WHERE status = 'Uploaded' ORDER BY created_at_utc LIMIT $maximum;";
        Add(command, "$maximum", maximum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) workflows.Add(MapWorkflow(reader));
        return workflows;
    }

    public async Task<VideoWorkflow?> TryClaimForDispatchAsync(
        string videoId, string instanceId, DateTimeOffset claimExpiresAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_workflows
            SET status = 'Queued', claimed_by = $instanceId, claim_expires_at_utc = $expiresAt,
                row_version = row_version + 1
            WHERE video_id = $videoId AND status = 'Uploaded'
            RETURNING video_id, original_file_name, content_type, declared_size_bytes, max_size_bytes,
                actual_size_bytes, upload_provider, transcode_provider, profile_name, status,
                source_bucket, source_key, source_version_id, source_local_relative_path,
                source_etag, source_checksum_sha256, source_identity_hash, external_job_id,
                progress_percent, claimed_by, claim_expires_at_utc, created_at_utc,
                upload_expires_at_utc, upload_started_at_utc, uploaded_at_utc, submitted_at_utc,
                processing_started_at_utc, completed_at_utc, error_code, error_message, row_version;
            """;
        Add(command, "$instanceId", instanceId);
        Add(command, "$expiresAt", Format(claimExpiresAtUtc));
        Add(command, "$videoId", videoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapWorkflow(reader) : null;
    }

    public async Task<int> RecoverStaleClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_workflows SET status = 'Uploaded', claimed_by = NULL, claim_expires_at_utc = NULL,
                row_version = row_version + 1
            WHERE status IN ('Queued', 'Validating') AND claim_expires_at_utc < $now;
            """;
        Add(command, "$now", Format(now));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkValidatingAsync(string videoId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video_workflows SET status = 'Validating', row_version = row_version + 1 WHERE video_id = $videoId AND status = 'Queued';";
        Add(command, "$videoId", videoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordProviderStartedAsync(
        string videoId, string? externalJobId, TranscodeJobStatus status, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (status is not (TranscodeJobStatus.Submitted or TranscodeJobStatus.Transcoding))
            throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_workflows
            SET status = $status, external_job_id = COALESCE(external_job_id, $jobId),
                submitted_at_utc = CASE WHEN $status = 'Submitted' THEN $now ELSE submitted_at_utc END,
                processing_started_at_utc = CASE WHEN $status = 'Transcoding' THEN $now ELSE processing_started_at_utc END,
                row_version = row_version + 1
            WHERE video_id = $videoId AND status = 'Validating'
              AND (external_job_id IS NULL OR external_job_id = $jobId);
            """;
        Add(command, "$status", status.ToString());
        Add(command, "$jobId", externalJobId);
        Add(command, "$now", Format(now));
        Add(command, "$videoId", videoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProviderStatusAsync(
        string videoId, string? expectedExternalJobId, TranscodeJobStatus status, double? progressPercent,
        string? errorCode, string? errorMessage, DateTimeOffset occurredAtUtc,
        IReadOnlyList<OutputArtifact>? artifacts, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        TranscodeJobStatus current;
        string? currentJobId;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT status, external_job_id FROM video_workflows WHERE video_id = $videoId;";
            Add(select, "$videoId", videoId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return;
            current = Enum.Parse<TranscodeJobStatus>(reader.GetString(0));
            currentJobId = GetString(reader, 1);
        }
        if (expectedExternalJobId is not null && !string.Equals(currentJobId, expectedExternalJobId, StringComparison.Ordinal)) return;
        if (IsTerminal(current) && current != status || Rank(status) < Rank(current)) return;

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE video_workflows
                SET status = $status, progress_percent = COALESCE($progress, progress_percent),
                    processing_started_at_utc = CASE WHEN $status = 'Transcoding' THEN COALESCE(processing_started_at_utc, $now) ELSE processing_started_at_utc END,
                    completed_at_utc = CASE WHEN $terminal = 1 THEN COALESCE(completed_at_utc, $now) ELSE completed_at_utc END,
                    error_code = $errorCode, error_message = $errorMessage,
                    claimed_by = CASE WHEN $terminal = 1 THEN NULL ELSE claimed_by END,
                    claim_expires_at_utc = CASE WHEN $terminal = 1 THEN NULL ELSE claim_expires_at_utc END,
                    row_version = row_version + 1 WHERE video_id = $videoId;
                """;
            Add(update, "$status", status.ToString());
            Add(update, "$progress", progressPercent);
            Add(update, "$now", Format(occurredAtUtc));
            Add(update, "$terminal", IsTerminal(status) ? 1 : 0);
            Add(update, "$errorCode", errorCode);
            Add(update, "$errorMessage", errorMessage);
            Add(update, "$videoId", videoId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        if (artifacts is not null)
            foreach (var artifact in artifacts) await InsertArtifactAsync(connection, transaction, artifact, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertArtifactAsync(
        SqliteConnection connection, SqliteTransaction transaction, OutputArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO output_artifacts(artifact_id, video_id, kind, name, location, content_type, size_bytes, created_at_utc)
            VALUES ($id, $videoId, $kind, $name, $location, $contentType, $size, $createdAt)
            ON CONFLICT(video_id, kind, name) DO UPDATE SET location = excluded.location,
                content_type = excluded.content_type, size_bytes = excluded.size_bytes,
                created_at_utc = excluded.created_at_utc;
            """;
        Add(command, "$id", artifact.ArtifactId);
        Add(command, "$videoId", artifact.VideoId);
        Add(command, "$kind", artifact.Kind);
        Add(command, "$name", artifact.Name);
        Add(command, "$location", artifact.Location);
        Add(command, "$contentType", artifact.ContentType);
        Add(command, "$size", artifact.SizeBytes);
        Add(command, "$createdAt", Format(artifact.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
