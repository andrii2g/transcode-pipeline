using Demo.Contracts.Enums;
using Demo.Contracts.Models;
using Demo.UploadApi.Application;
using Demo.UploadApi.Models;
using Demo.UploadApi.Persistence;

namespace Demo.UploadApi.Endpoints;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app, bool enableCompatibility)
    {
        app.MapGet("/transcodes/{videoId}", GetStatusAsync).WithName("GetTranscodeStatus");
        app.MapGet("/transcodes/{videoId}/result", GetResultAsync).WithName("GetTranscodeResult");
        if (enableCompatibility)
        {
#pragma warning disable ASPDEPR002
            app.MapPost("/transcodes", StartCompatibilityAsync).WithName("StartTranscodeCompatibility")
                .WithSummary("Deprecated compatibility route; automatic upload completion is the primary flow.")
                .WithOpenApi(operation => { operation.Deprecated = true; return operation; });
#pragma warning restore ASPDEPR002
        }
        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        string videoId, IWorkflowStore store, CancellationToken cancellationToken)
    {
        var workflow = await store.GetAsync(videoId, cancellationToken);
        return workflow is null
            ? UploadEndpoints.Problem(404, "WorkflowNotFound", "No workflow exists for the supplied videoId.")
            : Results.Ok(ToStatus(workflow));
    }

    private static async Task<IResult> GetResultAsync(
        string videoId, IWorkflowStore store, CancellationToken cancellationToken)
    {
        var workflow = await store.GetAsync(videoId, cancellationToken);
        if (workflow is null) return UploadEndpoints.Problem(404, "WorkflowNotFound", "No workflow exists for the supplied videoId.");
        if (workflow.Status != TranscodeJobStatus.Completed)
            return UploadEndpoints.Problem(409, "TranscodeNotComplete", $"The current status is '{workflow.Status}'.");
        var artifacts = await store.GetArtifactsAsync(videoId, cancellationToken);
        return Results.Ok(new TranscodeResultResponse(videoId, workflow.Status,
            artifacts.Select(a => new OutputArtifactResponse(a.Kind, a.Name, a.Location, a.ContentType, a.SizeBytes)).ToArray()));
    }

    private static async Task<IResult> StartCompatibilityAsync(
        StartTranscodeRequest request,
        ICompatibilityTranscodeService service,
        CancellationToken cancellationToken)
    {
        var workflow = await service.TriggerAsync(request.VideoId, cancellationToken);
        return workflow is null
            ? UploadEndpoints.Problem(404, "WorkflowNotFound", "No workflow exists for the supplied videoId.")
            : Results.Ok(ToStatus(workflow));
    }

    private static TranscodeStatusResponse ToStatus(VideoWorkflow workflow) => new(
        workflow.VideoId, workflow.Status, workflow.UploadProvider, workflow.TranscodeProvider,
        workflow.ProfileName, workflow.DeclaredSizeBytes, workflow.ActualSizeBytes, workflow.ExternalJobId,
        workflow.ProgressPercent, workflow.CreatedAtUtc, workflow.UploadedAtUtc, workflow.SubmittedAtUtc,
        workflow.ProcessingStartedAtUtc, workflow.CompletedAtUtc, workflow.ErrorCode, workflow.ErrorMessage);
}
