using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Demo.UploadApi.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Demo.UploadApi.Tests;

public sealed class HttpIntegrationTests : IClassFixture<LocalApiFactory>
{
    private readonly LocalApiFactory _factory;
    private readonly HttpClient _client;

    public HttpIntegrationTests(LocalApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Local_session_response_and_status_contract_are_provider_neutral()
    {
        using var response = await _client.PostAsJsonAsync("/uploads", new
        {
            fileName = "video.mp4",
            contentType = "video/mp4",
            sizeBytes = 10,
            profile = "web-standard-v1"
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("UploadPending", root.GetProperty("status").GetString());
        Assert.Equal(1024, root.GetProperty("maximumSizeBytes").GetInt64());
        Assert.Equal("LocalHttp", root.GetProperty("upload").GetProperty("provider").GetString());
        Assert.Equal("PUT", root.GetProperty("upload").GetProperty("method").GetString());
        Assert.True(root.GetProperty("upload").GetProperty("headers").TryGetProperty("X-Upload-Token", out _));

        var videoId = root.GetProperty("videoId").GetString();
        using var status = await _client.GetAsync($"/transcodes/{videoId}");
        status.EnsureSuccessStatusCode();
        using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("LocalHttp", statusJson.RootElement.GetProperty("uploadProvider").GetString());
        Assert.Equal("FFmpeg", statusJson.RootElement.GetProperty("transcodeProvider").GetString());
        Assert.False(statusJson.RootElement.TryGetProperty("sourceLocalRelativePath", out _));
    }

    [Fact]
    public async Task Oversize_create_returns_rfc7807_with_server_maximum()
    {
        using var response = await _client.PostAsJsonAsync("/uploads", new
        {
            fileName = "video.mp4",
            sizeBytes = 1025
        });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("FileSizeExceeded", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(1024, problem.RootElement.GetProperty("maximumSizeBytes").GetInt64());
    }

    [Fact]
    public async Task Raw_put_accepts_valid_body_and_rejects_content_length_over_session_limit()
    {
        var valid = await CreateAsync(10);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/uploads/{valid.VideoId}/content")
        {
            Content = new ByteArrayContent(new byte[10])
        };
        request.Headers.Add("X-Upload-Token", valid.Token);
        request.Content.Headers.ContentType = new("video/mp4");
        using var accepted = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var oversize = await CreateAsync(10);
        using var tooLarge = new HttpRequestMessage(HttpMethod.Put, $"/uploads/{oversize.VideoId}/content")
        {
            Content = new ByteArrayContent(new byte[1025])
        };
        tooLarge.Headers.Add("X-Upload-Token", oversize.Token);
        using var rejected = await _client.SendAsync(tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
    }

    [Fact]
    public async Task Compatibility_endpoint_is_disabled_by_default_and_result_conflicts_until_complete()
    {
        using var compatibility = await _client.PostAsJsonAsync("/transcodes", new { videoId = "anything" });
        Assert.Equal(HttpStatusCode.NotFound, compatibility.StatusCode);
        var upload = await CreateAsync(10);
        using var result = await _client.GetAsync($"/transcodes/{upload.VideoId}/result");
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
    }

    private async Task<(string VideoId, string Token)> CreateAsync(long size)
    {
        using var response = await _client.PostAsJsonAsync("/uploads", new { fileName = "video.mp4", contentType = "video/mp4", sizeBytes = size });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("videoId").GetString()!,
            json.RootElement.GetProperty("upload").GetProperty("headers").GetProperty("X-Upload-Token").GetString()!);
    }
}

public sealed class LocalApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"transcode-http-tests-{Guid.NewGuid():N}");
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaPipeline:UploadProvider"] = "LocalHttp",
            ["MediaPipeline:TranscodeProvider"] = "FFmpeg",
            ["MediaPipeline:DefaultProfile"] = "web-standard-v1",
            ["MediaPipeline:EnableManualTranscodeEndpoint"] = "false",
            ["UploadPolicy:DefaultMaxSizeBytes"] = "1024",
            ["UploadPolicy:AbsoluteMaxSizeBytes"] = "2048",
            ["UploadPolicy:Profiles:0"] = "web-standard-v1",
            ["WorkflowStore:Provider"] = "Sqlite",
            ["WorkflowStore:ConnectionString"] = $"Data Source={Path.Combine(_root, "workflows.db")}",
            ["LocalStorage:PublicBaseUrl"] = "https://example.test",
            ["LocalStorage:TemporaryUploadDirectory"] = Path.Combine(_root, "temp", "uploads"),
            ["LocalStorage:SourceDirectory"] = Path.Combine(_root, "source"),
            ["LocalStorage:TemporaryOutputDirectory"] = Path.Combine(_root, "temp", "outputs"),
            ["LocalStorage:OutputDirectory"] = Path.Combine(_root, "output"),
            ["Ffmpeg:FfmpegPath"] = "ffmpeg",
            ["Ffmpeg:FfprobePath"] = "ffprobe"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITranscodeProvider>();
            services.AddSingleton<ITranscodeProvider, FakeTranscodeProvider>();
        });
    }
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
