# On-Premises Workflow: Nginx + ASP.NET Core + FFmpeg

## Flow

```text
Client
  -> POST /uploads
  -> signed local PUT URL
  -> Nginx streaming proxy
  -> ASP.NET Core raw body endpoint
  -> temporary source file
  -> size/checksum validation
  -> atomic source publication
  -> local UploadCompleted notification
  -> dispatch worker
  -> ffprobe
  -> FFmpeg
  -> atomic output publication
```

## Nginx responsibilities

Nginx enforces only the infrastructure ceiling, for example 210 MiB. The application enforces each session's 20/50/200 MiB policy.

Required upload location settings:

```nginx
client_max_body_size 210m;
proxy_request_buffering off;
proxy_buffering off;
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
```

Disabling request buffering lets the application stop the upload as soon as its byte counter exceeds the session limit.

## ASP.NET Core responsibilities

Before reading:

- verify token;
- verify expiry;
- claim session;
- reject excessive Content-Length;
- reserve disk/concurrency capacity.

During reading:

- stream directly to disk;
- count actual bytes;
- calculate SHA-256;
- stop immediately over limit.

After reading:

- validate declared size policy;
- close/flush;
- atomically rename;
- persist Uploaded;
- publish notification;
- return 202.

## Directory layout

Keep temp and source on the same filesystem:

```text
/data/media/
  temp/
    uploads/
    transcodes/
  source/
    {videoId}/source.mp4
  output/
    {videoId}/published/
```

Do not use the original filename as a physical path.

## FFprobe

Run before FFmpeg to reject invalid media and collect duration, codecs, dimensions, and streams.

The source extension and HTTP content type are only hints.

## FFmpeg profile

Recommended first demo profile:

```text
HLS 720p
HLS 480p
AAC stereo
6-second HLS segments
MP4 720p faststart
```

Keep the same logical artifact names as the MediaConvert workflow so clients do not depend on the transcoder.

## Concurrency

FFmpeg is CPU-intensive. Configure a small maximum:

```text
development: 1
on-prem server: measured value, initially 1-2
```

A worker must claim workflows atomically. Multiple service instances must not run the same video.

## Cancellation and shutdown

On host shutdown:

- stop claiming work;
- cancel active transcodes;
- kill the process tree;
- mark the claim stale/retryable according to policy;
- remove temporary outputs later.

## Recovery

On startup:

- scan `Uploaded`;
- recover stale claims;
- inspect final source file for workflows stuck in `Uploading`;
- delete old `*.uploading`;
- delete/reconcile old temporary output directories.

## Serving results

Do not expose `/data/media` directly without authorization. Prefer an application download endpoint or a carefully controlled Nginx internal location.
