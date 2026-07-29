CREATE TABLE IF NOT EXISTS video_workflows
(
    video_id                    CHAR(36)      NOT NULL,
    original_file_name          VARCHAR(255)  NOT NULL,
    content_type                VARCHAR(255)  NULL,
    declared_size_bytes         BIGINT        NOT NULL,
    max_size_bytes              BIGINT        NOT NULL,
    actual_size_bytes           BIGINT        NULL,
    upload_provider             VARCHAR(32)   NOT NULL,
    transcode_provider          VARCHAR(32)   NOT NULL,
    profile_name                VARCHAR(128)  NOT NULL,
    status                      VARCHAR(32)   NOT NULL,
    source_bucket               VARCHAR(255)  NULL,
    source_key                  VARCHAR(1024) NULL,
    source_version_id           VARCHAR(255)  NULL,
    source_local_relative_path  VARCHAR(1024) NULL,
    source_etag                 VARCHAR(255)  NULL,
    source_checksum_sha256      CHAR(64)      NULL,
    source_identity_hash        CHAR(64)      NULL,
    external_job_id             VARCHAR(255)  NULL,
    progress_percent            DECIMAL(6,3)  NULL,
    claimed_by                  VARCHAR(255)  NULL,
    claim_expires_at_utc        DATETIME(6)   NULL,
    created_at_utc              DATETIME(6)   NOT NULL,
    upload_expires_at_utc       DATETIME(6)   NOT NULL,
    upload_started_at_utc       DATETIME(6)   NULL,
    uploaded_at_utc             DATETIME(6)   NULL,
    submitted_at_utc            DATETIME(6)   NULL,
    processing_started_at_utc   DATETIME(6)   NULL,
    completed_at_utc            DATETIME(6)   NULL,
    error_code                  VARCHAR(128)  NULL,
    error_message               TEXT          NULL,
    row_version                 BIGINT        NOT NULL DEFAULT 0,

    PRIMARY KEY(video_id),
    UNIQUE KEY ux_video_workflows_source_identity(source_identity_hash),
    UNIQUE KEY ux_video_workflows_external_job(external_job_id),
    KEY ix_video_workflows_dispatch(status, claim_expires_at_utc, created_at_utc),
    KEY ix_video_workflows_source(source_bucket, source_key(255), source_version_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS upload_sessions
(
    video_id              CHAR(36)     NOT NULL,
    token_hash            BINARY(32)   NULL,
    claimed_at_utc        DATETIME(6)  NULL,
    completed_at_utc      DATETIME(6)  NULL,
    provider_payload_json JSON         NULL,

    PRIMARY KEY(video_id),
    CONSTRAINT fk_upload_sessions_workflow
        FOREIGN KEY(video_id) REFERENCES video_workflows(video_id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS output_artifacts
(
    artifact_id       CHAR(36)      NOT NULL,
    video_id          CHAR(36)      NOT NULL,
    kind              VARCHAR(64)   NOT NULL,
    name              VARCHAR(255)  NOT NULL,
    location          VARCHAR(2048) NOT NULL,
    content_type      VARCHAR(255)  NULL,
    size_bytes        BIGINT        NULL,
    created_at_utc    DATETIME(6)   NOT NULL,

    PRIMARY KEY(artifact_id),
    UNIQUE KEY ux_output_artifacts(video_id, kind, name),
    CONSTRAINT fk_output_artifacts_workflow
        FOREIGN KEY(video_id) REFERENCES video_workflows(video_id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS processed_notifications
(
    topic_arn          VARCHAR(512) NOT NULL,
    message_id         CHAR(36)     NOT NULL,
    notification_type  VARCHAR(64)  NOT NULL,
    source_identity    VARCHAR(1536) NULL,
    received_at_utc    DATETIME(6)  NOT NULL,

    PRIMARY KEY(topic_arn, message_id),
    KEY ix_processed_notifications_source(source_identity(255))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
