PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS video_workflows
(
    video_id                   TEXT PRIMARY KEY,
    original_file_name         TEXT NOT NULL,
    content_type               TEXT NULL,
    declared_size_bytes        INTEGER NOT NULL,
    max_size_bytes             INTEGER NOT NULL,
    actual_size_bytes          INTEGER NULL,
    upload_provider            TEXT NOT NULL,
    transcode_provider         TEXT NOT NULL,
    profile_name               TEXT NOT NULL,
    status                     TEXT NOT NULL,
    source_bucket              TEXT NULL,
    source_key                 TEXT NULL,
    source_version_id          TEXT NULL,
    source_local_relative_path TEXT NULL,
    source_etag                TEXT NULL,
    source_checksum_sha256     TEXT NULL,
    source_identity_hash       TEXT NULL,
    external_job_id            TEXT NULL,
    progress_percent           REAL NULL,
    claimed_by                 TEXT NULL,
    claim_expires_at_utc       TEXT NULL,
    created_at_utc             TEXT NOT NULL,
    upload_expires_at_utc      TEXT NOT NULL,
    upload_started_at_utc      TEXT NULL,
    uploaded_at_utc            TEXT NULL,
    submitted_at_utc           TEXT NULL,
    processing_started_at_utc  TEXT NULL,
    completed_at_utc           TEXT NULL,
    error_code                 TEXT NULL,
    error_message              TEXT NULL,
    row_version                INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_video_workflows_source_s3
ON video_workflows(source_bucket, source_key, COALESCE(source_version_id, ''))
WHERE source_bucket IS NOT NULL AND source_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_video_workflows_source_identity
ON video_workflows(source_identity_hash)
WHERE source_identity_hash IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_video_workflows_external_job
ON video_workflows(external_job_id)
WHERE external_job_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_video_workflows_dispatch
ON video_workflows(status, claim_expires_at_utc, created_at_utc);

CREATE TABLE IF NOT EXISTS upload_sessions
(
    video_id             TEXT PRIMARY KEY,
    token_hash           BLOB NULL,
    claimed_at_utc       TEXT NULL,
    completed_at_utc     TEXT NULL,
    provider_payload_json TEXT NULL,
    FOREIGN KEY(video_id) REFERENCES video_workflows(video_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS output_artifacts
(
    artifact_id       TEXT PRIMARY KEY,
    video_id          TEXT NOT NULL,
    kind              TEXT NOT NULL,
    name              TEXT NOT NULL,
    location          TEXT NOT NULL,
    content_type      TEXT NULL,
    size_bytes        INTEGER NULL,
    created_at_utc    TEXT NOT NULL,
    FOREIGN KEY(video_id) REFERENCES video_workflows(video_id) ON DELETE CASCADE,
    UNIQUE(video_id, kind, name)
);

CREATE TABLE IF NOT EXISTS processed_notifications
(
    topic_arn          TEXT NOT NULL,
    message_id         TEXT NOT NULL,
    notification_type  TEXT NOT NULL,
    source_identity    TEXT NULL,
    received_at_utc    TEXT NOT NULL,
    PRIMARY KEY(topic_arn, message_id)
);

CREATE INDEX IF NOT EXISTS ix_processed_notifications_source
ON processed_notifications(source_identity)
WHERE source_identity IS NOT NULL;
