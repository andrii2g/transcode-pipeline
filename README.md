# Transcode Pipeline

`Demo.UploadApi` is one provider-neutral .NET 10 application with two selectable notification-driven workflows:

1. AWS: S3 pre-signed POST -> S3 Object Created -> Standard SNS -> HTTPS API -> MediaConvert -> EventBridge -> Standard SNS -> HTTPS API.
2. On premises: Nginx -> streaming ASP.NET Core PUT -> atomic local publication -> durable dispatcher -> ffprobe and FFmpeg.

No external queue, function, or key-value service is used. SQLite is the demo workflow store; `IWorkflowStore` and the supplied MySQL schema keep the persistence boundary portable.

## Client flow

```text
POST /uploads
  -> upload using the returned method, URL, headers, and form fields
  -> poll GET /transcodes/{videoId}
  -> fetch GET /transcodes/{videoId}/result after Completed
```

Normal clients never call `POST /transcodes`. That compatibility route is not mapped unless `MediaPipeline:EnableManualTranscodeEndpoint` is explicitly `true`.

Upload limits are enforced from server policy before dispatch: declared size, S3 POST `content-length-range`, local `Content-Length`, local streaming byte count, and authoritative source inspection.

## Run on premises

The checked-in API settings default to `LocalHttp` plus `FFmpeg`:

```powershell
dotnet restore
dotnet run --project src/Demo.UploadApi/Demo.UploadApi.csproj
```

Or start Nginx, the API, SQLite, ffprobe, and FFmpeg together:

```powershell
docker compose -f docker-compose.onprem.yml up --build
```

The proxied API is available at `http://localhost:8080`.

## AWS profile

Use `config/appsettings.Aws.example.json` as the configuration source. Replace every placeholder bucket, ARN, account, region, role, and hostname. The required public routes are:

```text
/notifications/aws/sns/uploads
/notifications/aws/sns/mediaconvert
```

Raw SNS delivery must remain disabled. The application verifies every signature and certificate chain and accepts only the exact configured topic ARN for each route.

## Validate

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
dotnet format --verify-no-changes
docker compose -f docker-compose.onprem.yml config
```

When the containers are running:

```powershell
docker compose -f docker-compose.onprem.yml exec nginx nginx -t
docker compose -f docker-compose.onprem.yml exec api ffmpeg -version
docker compose -f docker-compose.onprem.yml exec api ffprobe -version
```

## Documentation

- [API contract](API-CONTRACT.md)
- [AWS workflow](docs/AWS-S3-SNS-MEDIACONVERT.md)
- [SNS endpoint](docs/SNS-HTTP-ENDPOINT.md)
- [MediaConvert setup](docs/MEDIACONVERT-SETUP.md)
- [On-premises workflow](docs/ONPREM-NGINX-FFMPEG.md)
- [Operations and recovery](docs/OPERATIONS.md)

All sample ARNs, account IDs, bucket names, hostnames, paths, and certificates are placeholders.
