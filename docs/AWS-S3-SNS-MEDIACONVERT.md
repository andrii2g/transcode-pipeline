# AWS Workflow: S3 + SNS + MediaConvert

## Flow

```text
Client
  -> POST /uploads
  -> S3 pre-signed POST
  -> S3 ObjectCreated notification
  -> Standard SNS upload topic
  -> HTTPS application endpoint
  -> HeadObject and workflow update
  -> local dispatch worker
  -> MediaConvert CreateJob
  -> EventBridge job-state event
  -> Standard SNS MediaConvert topic
  -> HTTPS application endpoint
  -> workflow status/result update
```

## Resource layout

Use one AWS Region for:

- input S3 bucket;
- output S3 bucket;
- SNS topics;
- MediaConvert custom presets/template;
- EventBridge rule;
- application AWS SDK clients.

Suggested names:

```text
demo-video-input-<env>
demo-video-output-<env>
demo-video-upload-completed-<env>
demo-mediaconvert-job-state-<env>
Demo-Web-Transcode-v1
```

## 1. S3 buckets

Input:

```text
uploads/{videoId}/source/{generatedName}
```

Output:

```text
outputs/{videoId}/
```

Recommended:

- Block Public Access enabled.
- Bucket owner enforced; ACLs disabled.
- Default encryption.
- Lifecycle cleanup for old inputs and incomplete multipart uploads.
- Separate input/output permissions.

## 2. Upload session

The application creates a workflow and pre-signed POST. The policy must restrict:

- exact bucket/key;
- maximum size;
- metadata video ID;
- content type when known;
- expiry;
- encryption fields when required.

The client uploads directly to S3. Do not proxy AWS video bytes through the API.

## 3. S3 notification

Configure `s3:ObjectCreated:*` or specifically the POST event for prefix `uploads/`.

Destination:

```text
arn:aws:sns:<region>:<account>:demo-video-upload-completed-<env>
```

Use a Standard SNS topic.

The topic policy must allow `s3.amazonaws.com` to call `sns:Publish` only from the expected bucket/account.

When notification configuration is enabled, S3 sends a special test event with a different JSON shape. The endpoint must recognize it.

## 4. SNS HTTPS subscription

Subscribe:

```text
https://media-api.example.com/notifications/aws/sns/uploads
```

Requirements:

- public DNS name;
- trusted public CA certificate;
- endpoint deployed before subscription;
- confirmation message handling;
- raw message delivery disabled;
- exact TopicArn allow-list;
- signature verification;
- small body limit;
- idempotency.

Do not place a general API authorization scheme in front of this route that prevents SNS from reaching it. Authenticate SNS cryptographically instead.

## 5. Upload event processing

After signature verification:

1. parse the SNS envelope;
2. parse the S3 JSON in `Message`;
3. URL-decode object key;
4. load workflow;
5. call HeadObject;
6. verify metadata and size;
7. atomically mark uploaded;
8. signal dispatcher;
9. return 204.

The dispatcher starts MediaConvert outside the HTTP request.

## 6. MediaConvert completion

MediaConvert emits job state events to EventBridge. Create a rule whose target is the MediaConvert SNS topic.

Subscribe:

```text
https://media-api.example.com/notifications/aws/sns/mediaconvert
```

Use user metadata in the job:

```text
videoId
workflowName
profile
```

The handler uses `videoId` plus external job ID to update the correct workflow.

## 7. Failure behavior

### Permanent upload rejection

Examples:

- unexpected key;
- unknown video ID;
- size too large;
- metadata mismatch.

Action:

- mark rejected;
- optionally delete/quarantine source;
- return 204.

### Transient application/AWS failure

Examples:

- database unavailable;
- HeadObject timeout;
- temporary AWS SDK failure.

Action:

- return 503;
- SNS retries according to delivery policy.

### Duplicate delivery

Return 204 after finding the notification already processed.

## 8. Public endpoint caution

Without an intermediate durable external queue, the HTTPS application endpoint is part of the delivery path. Deploy multiple healthy API instances behind a load balancer, use database idempotency, monitor SNS delivery failures, and configure a suitable HTTP/S delivery policy.
