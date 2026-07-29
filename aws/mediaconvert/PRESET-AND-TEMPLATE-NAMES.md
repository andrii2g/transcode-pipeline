# Preset and Template Inventory

## Profile: `web-standard-v1`

### Custom output presets

```text
Demo-HLS-720p-AVC-v1
Demo-HLS-480p-AVC-v1
Demo-MP4-720p-AVC-AAC-v1
```

### Custom job template

```text
Demo-Web-Transcode-v1
```

### Expected output paths

```text
s3://<output-bucket>/outputs/{videoId}/hls/
s3://<output-bucket>/outputs/{videoId}/file/
```

### Expected artifacts

```text
HLS master playlist
HLS rendition playlists and segments
standalone MP4
```

Create and validate the exact settings in the MediaConvert console, then export/capture the job JSON. Keep the validated JSON under version control if the organization treats infrastructure configuration as code.
