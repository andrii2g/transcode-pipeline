# Getting Started

## On-premises demo

```powershell
docker compose -f docker-compose.onprem.yml config
docker compose -f docker-compose.onprem.yml up --build
```

Create a session with `POST /uploads`, PUT the raw bytes to the returned URL with the returned `X-Upload-Token`, and poll `GET /transcodes/{videoId}`. Do not call `POST /transcodes`.

Verify the container tools:

```powershell
docker compose -f docker-compose.onprem.yml exec nginx nginx -t
docker compose -f docker-compose.onprem.yml exec api ffmpeg -version
docker compose -f docker-compose.onprem.yml exec api ffprobe -version
```

## AWS demo

1. Copy `config/appsettings.Aws.example.json` into the deployment configuration.
2. Replace all placeholders.
3. Prepare S3 with [aws-s3-checklist.md](aws-s3-checklist.md).
4. Prepare both Standard SNS topics with [aws-sns-checklist.md](aws-sns-checklist.md).
5. Prepare MediaConvert and EventBridge with [aws-mediaconvert-checklist.md](aws-mediaconvert-checklist.md).
6. Deploy the HTTPS API before subscribing the SNS endpoints.
7. Keep raw SNS message delivery disabled.

AWS CLI credentials are used by the CLI setup steps. Application credentials should use the IAM policy in `aws/iam/application-policy.json`; do not commit access keys.

## Build and test

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
dotnet format --verify-no-changes
```

The successful client sequence is:

```text
POST /uploads
upload to returned provider instruction
GET /transcodes/{videoId}
GET /transcodes/{videoId}/result
```
