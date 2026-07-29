# MediaConvert Checklist

MediaConvert is configured by Region. Do not run endpoint discovery and do not configure an account-specific service URL.

1. Create the service role using `aws/iam/mediaconvert-trust-policy.json` and `aws/iam/mediaconvert-service-role-policy.json`.
2. Create these custom output presets:
   - `Demo-HLS-720p-AVC-v1`
   - `Demo-HLS-480p-AVC-v1`
   - `Demo-MP4-720p-AVC-AAC-v1`
3. Create the `Demo-Web-Transcode-v1` template from `aws/mediaconvert/job-template-web-standard-v1.json`.
4. Create the EventBridge rule from `aws/eventbridge/mediaconvert-event-pattern.json`.
5. Target the MediaConvert Standard SNS topic and apply its exact-source policy.

Create or update the template:

```powershell
aws mediaconvert create-job-template --region <region> --cli-input-json file://aws/mediaconvert/job-template-web-standard-v1.json
```

No `--endpoint-url` is required.

The application supplies per job:

- source S3 URI;
- HLS and file output destinations;
- service role and optional queue;
- `videoId`, `workflowName`, and `profile` metadata;
- deterministic `ClientRequestToken`.

Validate:

```powershell
aws mediaconvert get-job-template --region <region> --name Demo-Web-Transcode-v1
aws events describe-rule --region <region> --name demo-mediaconvert-job-state-prod
aws events list-targets-by-rule --region <region> --rule demo-mediaconvert-job-state-prod
```

Run one console job with the template before application testing and verify both the HLS master/renditions and MP4 output.
