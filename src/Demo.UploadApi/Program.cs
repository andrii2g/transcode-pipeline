using System.Text.Json.Serialization;
using Demo.Contracts.Enums;
using Demo.UploadApi.DependencyInjection;
using Demo.UploadApi.Endpoints;
using Demo.UploadApi.Options;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddUserSecrets<Program>(optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddMediaPipeline(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => TypedResults.Ok(new
{
    service = "Demo.UploadApi",
    version = "v1"
}));

var pipeline = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MediaPipelineOptions>>().Value;
app.MapUploadEndpoints();
app.MapWorkflowEndpoints(pipeline.EnableManualTranscodeEndpoint);
if (pipeline.UploadProvider == UploadProviderKind.S3PresignedPost)
{
    app.MapAwsNotificationEndpoints();
}

app.Run();

public partial class Program;
