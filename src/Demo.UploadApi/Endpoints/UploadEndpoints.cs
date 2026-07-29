using Demo.UploadApi.Application;
using Demo.UploadApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace Demo.UploadApi.Endpoints;

public static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/uploads", CreateAsync).WithName("CreateUpload")
            .WithSummary("Create a durable upload session and provider-specific upload instructions.");
        app.MapPut("/uploads/{videoId}/content", UploadLocalAsync).WithName("UploadLocalContent")
            .WithSummary("Stream a local video into a one-time upload session.");
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateUploadRequest request,
        ICreateUploadSessionService service,
        IUploadLimitResolver limitResolver,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await service.CreateAsync(request.FileName, request.ContentType, request.SizeBytes,
                request.Profile, cancellationToken);
            return Results.Ok(new CreateUploadResponse(created.Workflow.VideoId, created.Workflow.Status,
                created.Workflow.MaximumSizeBytes, created.Workflow.UploadExpiresAtUtc,
                new UploadInstructionResponse(created.Instruction.Provider, created.Instruction.Method,
                    created.Instruction.Url.ToString(), created.Instruction.Headers, created.Instruction.FormFields)));
        }
        catch (UploadRequestException exception)
        {
            return Problem(exception.StatusCode, exception.Code, exception.Message,
                exception.Code == "FileSizeExceeded" ? new Dictionary<string, object?>
                {
                    ["maximumSizeBytes"] = limitResolver.Resolve()
                } : null);
        }
    }

    private static async Task<IResult> UploadLocalAsync(
        string videoId,
        HttpContext context,
        ILocalUploadService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = context.Request.Headers["X-Upload-Token"].ToString();
            var contentType = context.Request.ContentType?.Split(';', 2)[0];
            await service.UploadAsync(videoId, token, context.Request.Body, context.Request.ContentLength,
                contentType, cancellationToken);
            return Results.Accepted($"/transcodes/{videoId}");
        }
        catch (LocalUploadException exception)
        {
            return Problem(exception.StatusCode, exception.Code, exception.Message);
        }
    }

    internal static IResult Problem(int status, string code, string detail,
        IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails { Status = status, Title = Title(code), Detail = detail };
        problem.Extensions["code"] = code;
        if (extensions is not null)
            foreach (var pair in extensions) if (pair.Value is not null) problem.Extensions[pair.Key] = pair.Value;
        return Results.Problem(problem);
    }

    private static string Title(string code) => code switch
    {
        "FileSizeExceeded" => "Video file is too large",
        "UploadTokenInvalid" => "Upload token is invalid",
        "UploadExpired" => "Upload session has expired",
        _ => "Upload request failed"
    };
}
