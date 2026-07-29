using System.Globalization;
using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Persistence;

public sealed partial class SqliteWorkflowStore(IOptions<WorkflowStoreOptions> options) : IWorkflowStore
{
    private const string WorkflowColumns = """
        video_id, original_file_name, content_type, declared_size_bytes, max_size_bytes,
        actual_size_bytes, upload_provider, transcode_provider, profile_name, status,
        source_bucket, source_key, source_version_id, source_local_relative_path,
        source_etag, source_checksum_sha256, source_identity_hash, external_job_id,
        progress_percent, claimed_by, claim_expires_at_utc, created_at_utc,
        upload_expires_at_utc, upload_started_at_utc, uploaded_at_utc, submitted_at_utc,
        processing_started_at_utc, completed_at_utc, error_code, error_message, row_version
        """;

    private readonly string _connectionString = options.Value.ConnectionString;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseDirectory();
        await using var connection = await OpenAsync(cancellationToken);
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sqlite.sql"), cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateAsync(VideoWorkflow workflow, UploadSession session, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO video_workflows
                (video_id, original_file_name, content_type, declared_size_bytes, max_size_bytes,
                 upload_provider, transcode_provider, profile_name, status, source_bucket, source_key,
                 source_version_id, source_local_relative_path, created_at_utc, upload_expires_at_utc)
                VALUES
                ($videoId, $fileName, $contentType, $declaredSize, $maximumSize, $uploadProvider,
                 $transcodeProvider, $profile, $status, $bucket, $key, $versionId, $localPath,
                 $createdAt, $expiresAt);
                """;
            Add(command, "$videoId", workflow.VideoId);
            Add(command, "$fileName", workflow.OriginalFileName);
            Add(command, "$contentType", workflow.ContentType);
            Add(command, "$declaredSize", workflow.DeclaredSizeBytes);
            Add(command, "$maximumSize", workflow.MaximumSizeBytes);
            Add(command, "$uploadProvider", workflow.UploadProvider.ToString());
            Add(command, "$transcodeProvider", workflow.TranscodeProvider.ToString());
            Add(command, "$profile", workflow.ProfileName);
            Add(command, "$status", workflow.Status.ToString());
            Add(command, "$bucket", workflow.Source.Bucket);
            Add(command, "$key", workflow.Source.Key);
            Add(command, "$versionId", workflow.Source.VersionId);
            Add(command, "$localPath", workflow.Source.LocalRelativePath);
            Add(command, "$createdAt", Format(workflow.CreatedAtUtc));
            Add(command, "$expiresAt", Format(workflow.UploadExpiresAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO upload_sessions(video_id, token_hash, provider_payload_json) VALUES ($videoId, $tokenHash, $payload);";
            Add(command, "$videoId", session.VideoId);
            Add(command, "$tokenHash", session.TokenHash);
            Add(command, "$payload", session.ProviderPayloadJson);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<VideoWorkflow?> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {WorkflowColumns} FROM video_workflows WHERE video_id = $videoId;";
        Add(command, "$videoId", videoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapWorkflow(reader) : null;
    }

    public async Task<UploadSession?> GetSessionAsync(string videoId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.video_id, s.token_hash, w.upload_expires_at_utc, w.max_size_bytes,
                   w.declared_size_bytes, s.claimed_at_utc, s.completed_at_utc, s.provider_payload_json
            FROM upload_sessions s JOIN video_workflows w ON w.video_id = s.video_id
            WHERE s.video_id = $videoId;
            """;
        Add(command, "$videoId", videoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new UploadSession(reader.GetString(0), reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1),
            Parse(reader.GetString(2)), reader.GetInt64(3), reader.GetInt64(4), GetDate(reader, 5),
            GetDate(reader, 6), GetString(reader, 7));
    }

    public async Task<IReadOnlyList<OutputArtifact>> GetArtifactsAsync(string videoId, CancellationToken cancellationToken)
    {
        var artifacts = new List<OutputArtifact>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_id, video_id, kind, name, location, content_type, size_bytes, created_at_utc FROM output_artifacts WHERE video_id = $videoId ORDER BY kind, name;";
        Add(command, "$videoId", videoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            artifacts.Add(new OutputArtifact(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), GetString(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6), Parse(reader.GetString(7))));
        }
        return artifacts;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private void EnsureDatabaseDirectory()
    {
        var source = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(source) || source == ":memory:") return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(source));
        if (directory is not null) Directory.CreateDirectory(directory);
    }

    private static VideoWorkflow MapWorkflow(SqliteDataReader reader)
    {
        var uploadProvider = Enum.Parse<UploadProviderKind>(reader.GetString(6));
        var bucket = GetString(reader, 10);
        var key = GetString(reader, 11);
        var versionId = GetString(reader, 12);
        var localPath = GetString(reader, 13);
        var identity = uploadProvider == UploadProviderKind.S3PresignedPost
            ? $"s3://{bucket}/{key}#{versionId ?? string.Empty}" : $"local:{localPath}";
        return new VideoWorkflow
        {
            VideoId = reader.GetString(0),
            OriginalFileName = reader.GetString(1),
            ContentType = GetString(reader, 2),
            DeclaredSizeBytes = reader.GetInt64(3),
            MaximumSizeBytes = reader.GetInt64(4),
            ActualSizeBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            UploadProvider = uploadProvider,
            TranscodeProvider = Enum.Parse<TranscodeProviderKind>(reader.GetString(7)),
            ProfileName = reader.GetString(8),
            Status = Enum.Parse<TranscodeJobStatus>(reader.GetString(9)),
            Source = new SourceLocator(uploadProvider, identity, bucket, key, versionId, localPath),
            SourceETag = GetString(reader, 14),
            SourceChecksumSha256 = GetString(reader, 15),
            SourceIdentityHash = GetString(reader, 16),
            ExternalJobId = GetString(reader, 17),
            ProgressPercent = reader.IsDBNull(18) ? null : reader.GetDouble(18),
            ClaimedBy = GetString(reader, 19),
            ClaimExpiresAtUtc = GetDate(reader, 20),
            CreatedAtUtc = Parse(reader.GetString(21)),
            UploadExpiresAtUtc = Parse(reader.GetString(22)),
            UploadStartedAtUtc = GetDate(reader, 23),
            UploadedAtUtc = GetDate(reader, 24),
            SubmittedAtUtc = GetDate(reader, 25),
            ProcessingStartedAtUtc = GetDate(reader, 26),
            CompletedAtUtc = GetDate(reader, 27),
            ErrorCode = GetString(reader, 28),
            ErrorMessage = GetString(reader, 29),
            RowVersion = reader.GetInt64(30)
        };
    }

    private static int Rank(TranscodeJobStatus status) => status switch
    {
        TranscodeJobStatus.UploadPending => 0,
        TranscodeJobStatus.Uploading => 1,
        TranscodeJobStatus.Uploaded => 2,
        TranscodeJobStatus.Queued => 3,
        TranscodeJobStatus.Validating => 4,
        TranscodeJobStatus.Submitted => 5,
        TranscodeJobStatus.Transcoding => 6,
        _ => 7
    };

    private static bool IsTerminal(TranscodeJobStatus status) => status is TranscodeJobStatus.Completed or
        TranscodeJobStatus.Failed or TranscodeJobStatus.Canceled or TranscodeJobStatus.UploadRejected or TranscodeJobStatus.Expired;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string? GetString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? GetDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
