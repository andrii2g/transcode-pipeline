using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Demo.UploadApi.Infrastructure.Aws;
using Demo.UploadApi.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Endpoints;

public static class AwsNotificationEndpoints
{
    public static IEndpointRouteBuilder MapAwsNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/notifications/aws/sns/uploads",
            (HttpContext context, AwsNotificationService service, IOptions<AwsNotificationOptions> options,
                CancellationToken cancellationToken) =>
                HandleAsync(context, service.HandleUploadAsync, options.Value.RequestBodyLimitBytes, cancellationToken));
        app.MapPost("/notifications/aws/sns/mediaconvert",
            (HttpContext context, AwsNotificationService service, IOptions<AwsNotificationOptions> options,
                CancellationToken cancellationToken) =>
                HandleAsync(context, service.HandleMediaConvertAsync, options.Value.RequestBodyLimitBytes, cancellationToken));
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        Func<SnsEnvelope, CancellationToken, Task> handler,
        long maximumBodyBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength > maximumBodyBytes)
                return UploadEndpoints.Problem(413, "SnsMessageTooLarge", "The SNS envelope exceeds the configured request limit.");
            await using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            long total = 0;
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > maximumBodyBytes)
                    return UploadEndpoints.Problem(413, "SnsMessageTooLarge", "The SNS envelope exceeds the configured request limit.");
                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            body.Position = 0;
            var envelope = await JsonSerializer.DeserializeAsync<SnsEnvelope>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false }, cancellationToken)
                ?? throw new JsonException("SNS envelope was empty.");
            await handler(envelope, cancellationToken);
            return Results.NoContent();
        }
        catch (UnauthorizedAccessException exception)
        {
            return UploadEndpoints.Problem(403, "SnsTopicNotAllowed", exception.Message);
        }
        catch (CryptographicException exception)
        {
            return UploadEndpoints.Problem(403, "SnsSignatureInvalid", exception.Message);
        }
        catch (JsonException exception)
        {
            return UploadEndpoints.Problem(400, "SnsMessageMalformed", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return UploadEndpoints.Problem(400, "SnsMessageInvalid", exception.Message);
        }
        catch (AmazonServiceException)
        {
            return UploadEndpoints.Problem(503, "AwsTemporarilyUnavailable", "An AWS dependency is temporarily unavailable.");
        }
        catch (SqliteException)
        {
            return UploadEndpoints.Problem(503, "WorkflowStoreUnavailable", "The workflow store is temporarily unavailable.");
        }
    }
}
