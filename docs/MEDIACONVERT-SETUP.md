# AWS Elemental MediaConvert Setup

## 1. Select Region

Create custom presets and job templates in the same Region as the source/output buckets and application MediaConvert client.

Configure a Region, not a manually discovered account endpoint.

## 2. Create MediaConvert service role

Trust principal:

```text
mediaconvert.amazonaws.com
```

Minimum data permissions:

- `s3:GetObject` for input prefix;
- `s3:ListBucket`/`s3:GetBucketLocation` where required;
- `s3:PutObject` for output prefix;
- KMS decrypt/data-key permissions when using customer-managed keys.

The application role separately needs:

- `mediaconvert:CreateJob`;
- `mediaconvert:GetJob` for reconciliation;
- `iam:PassRole` restricted to the exact MediaConvert service role.

## 3. Decide outputs

Recommended demo:

| Group | Outputs |
|---|---|
| Apple HLS | 720p AVC video, 480p AVC video, AAC audio |
| File | 720p MP4 AVC/AAC |

Suggested logical settings:

### Video

- codec: H.264/AVC;
- rate control: QVBR;
- quality level: start around 7 and validate visually;
- follow source frame rate;
- 720p maximum for file output;
- 720p and 480p HLS renditions;
- progressive output;
- GOP aligned to segment boundaries.

### Audio

- AAC LC;
- stereo;
- 48 kHz;
- 128 kbps as a practical starting point.

### HLS

- VOD playlist;
- approximately 6-second segments;
- consistent segment/GOP alignment;
- destination supplied per job.

Exact production values must be selected from real source content, quality requirements, player behavior, and cost testing.

## 4. Create output presets

Use custom presets for individual outputs:

```text
Demo-HLS-720p-AVC-v1
Demo-HLS-480p-AVC-v1
Demo-MP4-720p-AVC-AAC-v1
```

Recommended procedure:

1. Open MediaConvert Output presets.
2. Find a suitable system preset.
3. Duplicate it into a custom preset.
4. Set explicit codec, resolution, bitrate/QVBR, audio, and container values.
5. Version the name.
6. Add a clear description/category.
7. Export or capture the settings in repository documentation.
8. Never edit a production preset in place; create `v2`.

Presets apply to one output.

## 5. Create job template

Create:

```text
Demo-Web-Transcode-v1
```

The template should include:

- input selector defaults;
- audio selector defaults;
- HLS output group;
- HLS output entries using the custom presets;
- file output group;
- MP4 output entry using the custom preset;
- common timecode and acceleration policy;
- status update interval if required.

The application supplies at runtime:

- input S3 URI;
- HLS destination;
- file destination;
- IAM service role;
- optional queue;
- metadata;
- stable client request token.

Job templates apply to the full job; output presets apply to individual outputs.

## 6. Validate through the console first

Before application integration:

1. Create a job manually.
2. Select the source object.
3. Select the custom template.
4. Set output destinations.
5. Run the job.
6. Verify HLS in the target player.
7. Verify MP4.
8. Inspect audio/video dimensions and bitrate.
9. Inspect the console's job JSON.
10. Save the validated JSON/settings as the implementation reference.

MediaConvert job JSON has many interdependent settings. Use the console as the validation/builder tool instead of hand-authoring a large untested JSON document.

## 7. Application job request

The request factory should include:

```text
Role = configured role ARN
JobTemplate = Demo-Web-Transcode-v1
Queue = optional queue ARN
Settings.Inputs[0].FileInput = s3://input-bucket/key
Output destinations = s3://output-bucket/outputs/{videoId}/...
UserMetadata.videoId = video ID
UserMetadata.profile = web-standard
UserMetadata.workflowName = configured workflow
ClientRequestToken = stable deterministic token
```

Test the exact template override behavior against AWS. If the SDK/template merge does not preserve output definitions as expected, retrieve/cache the template settings and patch only dynamic destinations in a request factory.

## 8. MediaConvert event notifications

Create EventBridge rule for:

```text
INPUT_INFORMATION
PROGRESSING
STATUS_UPDATE
COMPLETE
ERROR
CANCELED
```

Target the MediaConvert SNS topic.

On COMPLETE, persist output information from the event and/or reconcile with GetJob.

## 9. Versioning

Treat profiles as immutable:

```text
web-standard-v1 -> Demo-Web-Transcode-v1
web-standard-v2 -> Demo-Web-Transcode-v2
```

Persist the profile/template version on each workflow so old jobs remain explainable.

## 10. Operational checks

Monitor:

- CreateJob failures;
- job ERROR codes;
- age of Submitted/Transcoding workflows;
- SNS delivery failures;
- output object presence;
- cost and output minutes;
- input files retained longer than policy.
