# Operations and Recovery

## Metrics

Expose counters/gauges:

```text
uploads_created_total
uploads_rejected_total{reason}
uploads_completed_total{provider}
sns_notifications_total{topic,type,outcome}
sns_signature_failures_total
transcode_claims_total{provider}
transcodes_started_total{provider}
transcodes_completed_total{provider}
transcodes_failed_total{provider,reason}
transcode_active{provider}
workflow_oldest_pending_seconds{status}
local_upload_bytes_total
local_temp_bytes
```

## Logs

Use correlation fields:

```text
videoId
notificationId
snsMessageId
provider
externalJobId
workflowStatus
```

Redact all bearer/signed material.

## Health checks

- relational workflow store;
- local temp/source/output writable and disk space;
- FFmpeg and ffprobe executable;
- AWS credentials/region configuration;
- optional S3 HeadBucket;
- optional MediaConvert lightweight reconciliation capability.

Do not make public liveness depend on every external service. Separate liveness/readiness.

## Cleanup

- expired unused upload sessions;
- stale temporary uploads;
- stale FFmpeg output directories;
- old input objects/files according to retention;
- processed notification rows after a safe retention window;
- completed workflow history according to policy.

## Reconciliation

Periodic tasks:

```text
Uploaded -> signal dispatcher
Queued/Validating with expired claim -> release/retry
Submitted MediaConvert older than threshold -> GetJob
Uploading with final local source present -> repair to Uploaded
Completed but artifacts absent -> mark/investigate inconsistency
```

## Alerts

- SNS delivery failures;
- many signature failures;
- oldest Uploaded workflow exceeds threshold;
- MediaConvert ERROR spike;
- FFmpeg failure spike;
- disk below reserve;
- database unavailable;
- notification endpoint 5xx rate.

## Rollout

1. Add new schema and provider-neutral status.
2. Deploy with manual endpoint still enabled.
3. Test AWS SNS subscription and confirmation.
4. Enable automatic AWS upload notification.
5. Observe duplicate/idempotency behavior.
6. Disable manual endpoint for normal clients.
7. Deploy local profile and verify FFmpeg.
8. Remove obsolete S3 manifest authority after migration.
