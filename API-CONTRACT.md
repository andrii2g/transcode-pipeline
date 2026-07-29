# API Contract

## `POST /uploads`

Request:

```json
{
  "fileName": "interview.mp4",
  "contentType": "video/mp4",
  "sizeBytes": 15728640,
  "profile": "web-standard"
}
```

Validation:

- filename required;
- supported extension;
- positive size;
- known profile;
- declared size not greater than server-resolved maximum.

Oversize:

```http
HTTP/1.1 413 Payload Too Large
```

```json
{
  "title": "Video file is too large",
  "status": 413,
  "code": "FileSizeExceeded",
  "maximumSizeBytes": 20971520
}
```

### AWS response

```json
{
  "videoId": "019c...",
  "status": "UploadPending",
  "maximumSizeBytes": 20971520,
  "expiresAtUtc": "2026-07-29T08:30:00Z",
  "upload": {
    "provider": "S3PresignedPost",
    "method": "POST",
    "url": "https://input-bucket.s3.region.amazonaws.com/",
    "headers": {},
    "formFields": {
      "key": "uploads/019c.../source/interview.mp4",
      "Content-Type": "video/mp4",
      "x-amz-meta-video-id": "019c...",
      "success_action_status": "204",
      "policy": "...",
      "x-amz-algorithm": "...",
      "x-amz-credential": "...",
      "x-amz-date": "...",
      "x-amz-signature": "..."
    }
  }
}
```

The browser must submit all form fields exactly and add the file part last.

### On-premises response

```json
{
  "videoId": "019c...",
  "status": "UploadPending",
  "maximumSizeBytes": 20971520,
  "expiresAtUtc": "2026-07-29T08:30:00Z",
  "upload": {
    "provider": "LocalHttp",
    "method": "PUT",
    "url": "https://media.example.com/uploads/019c.../content",
    "headers": {
      "X-Upload-Token": "one-time-secret",
      "Content-Type": "video/mp4"
    },
    "formFields": {}
  }
}
```

## `PUT /uploads/{videoId}/content`

Local/on-premises only. Body is the raw video stream.

Success:

```http
HTTP/1.1 202 Accepted
Location: /transcodes/{videoId}
```

Failures:

| HTTP | Code |
|---|---|
| 401 | `UploadTokenInvalid` |
| 409 | `UploadAlreadyUsed` |
| 410 | `UploadExpired` |
| 413 | `FileSizeExceeded` |
| 415 | `UnsupportedMediaType` |
| 422 | `DeclaredSizeMismatch` |
| 503 | `UploadCapacityUnavailable` |

## `POST /notifications/aws/sns/uploads`

AWS only. Accepts SNS envelope JSON with `Content-Type: text/plain; charset=UTF-8`.

Supported message types:

- `SubscriptionConfirmation`
- `Notification`
- optionally acknowledge `UnsubscribeConfirmation`

For notifications, `Message` contains either:

- S3 `Records` payload;
- S3 `TestEvent`.

No business response body is required. Return 204 after durable handling.

## `POST /notifications/aws/sns/mediaconvert`

Accepts SNS envelope whose `Message` is an EventBridge MediaConvert event.

Return 204 after durable idempotent update.

## `POST /transcodes`

Compatibility only:

- disabled by default;
- deprecated;
- invokes the same idempotent completion/dispatch logic;
- never directly runs the provider from endpoint code.

## `GET /transcodes/{videoId}`

Provider-neutral response:

```json
{
  "videoId": "019c...",
  "status": "Transcoding",
  "uploadProvider": "LocalHttp",
  "transcodeProvider": "FFmpeg",
  "profile": "web-standard",
  "declaredSizeBytes": 15728640,
  "sourceSizeBytes": 15728640,
  "externalJobId": null,
  "progressPercent": 42.5,
  "createdAtUtc": "...",
  "uploadedAtUtc": "...",
  "submittedAtUtc": null,
  "startedAtUtc": "...",
  "completedAtUtc": null,
  "errorCode": null,
  "errorMessage": null
}
```

Do not expose local paths, upload token material, S3 policy fields, or SNS subscription tokens.

## `GET /transcodes/{videoId}/result`

```json
{
  "videoId": "019c...",
  "status": "Completed",
  "artifacts": [
    {
      "kind": "HlsMasterPlaylist",
      "name": "master.m3u8",
      "location": "outputs/019c.../hls/master.m3u8",
      "contentType": "application/vnd.apple.mpegurl"
    },
    {
      "kind": "Mp4",
      "name": "video.mp4",
      "location": "outputs/019c.../file/video.mp4",
      "contentType": "video/mp4"
    }
  ]
}
```

For S3, generate download URLs on demand or return opaque keys. For local storage, return a controlled download route, never a physical filesystem path.
