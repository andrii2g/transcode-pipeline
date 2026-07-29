using Demo.Contracts.Enums;
using Demo.UploadApi.Application;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Tests;

public sealed class UploadSessionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_file_name_is_rejected(string fileName)
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var service = CreateService(harness, 20 * 1024 * 1024);
        var error = await Assert.ThrowsAsync<UploadRequestException>(() =>
            service.CreateAsync(fileName, "video/mp4", 1, null, CancellationToken.None));
        Assert.Equal("FileNameRequired", error.Code);
    }

    [Fact]
    public async Task Unsupported_extension_is_rejected()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var error = await Assert.ThrowsAsync<UploadRequestException>(() =>
            CreateService(harness, 20 * 1024 * 1024).CreateAsync("video.exe", null, 1, null, CancellationToken.None));
        Assert.Equal("UnsupportedExtension", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_size_is_rejected(long size)
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var error = await Assert.ThrowsAsync<UploadRequestException>(() =>
            CreateService(harness, 20 * 1024 * 1024).CreateAsync("video.mp4", null, size, null, CancellationToken.None));
        Assert.Equal("InvalidSize", error.Code);
    }

    [Fact]
    public async Task Exact_20_mib_is_accepted_and_one_more_byte_is_rejected()
    {
        const long maximum = 20L * 1024 * 1024;
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var service = CreateService(harness, maximum);
        var created = await service.CreateAsync("video.mp4", "video/mp4", maximum, null, CancellationToken.None);
        Assert.Equal(maximum, created.Workflow.MaximumSizeBytes);
        var error = await Assert.ThrowsAsync<UploadRequestException>(() =>
            service.CreateAsync("other.mp4", null, maximum + 1, null, CancellationToken.None));
        Assert.Equal("FileSizeExceeded", error.Code);
        Assert.Equal(413, error.StatusCode);
    }

    [Fact]
    public void Named_50_and_200_mib_policies_are_server_resolved()
    {
        var resolver = new UploadLimitResolver(TestOptions.Policy());
        Assert.Equal(50L * 1024 * 1024, resolver.Resolve("extended"));
        Assert.Equal(200L * 1024 * 1024, resolver.Resolve("large"));
        Assert.Equal(20L * 1024 * 1024, resolver.Resolve("unknown"));
    }

    [Fact]
    public async Task Unknown_profile_is_rejected()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var error = await Assert.ThrowsAsync<UploadRequestException>(() =>
            CreateService(harness, 1024).CreateAsync("video.mp4", null, 1, "unknown", CancellationToken.None));
        Assert.Equal("UnknownProfile", error.Code);
    }

    [Fact]
    public async Task Client_file_name_never_controls_local_physical_path_and_only_token_hash_is_stored()
    {
        await using var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var provider = new FakeUploadProvider();
        var service = CreateService(harness, 1024, provider);
        var created = await service.CreateAsync("../../evil.mp4", "video/mp4", 12, null, CancellationToken.None);
        Assert.Equal("evil.mp4", created.Workflow.OriginalFileName);
        Assert.DoesNotContain("evil", created.Workflow.Source.LocalRelativePath, StringComparison.OrdinalIgnoreCase);
        var token = created.Instruction.Headers["X-Upload-Token"];
        var session = await harness.Store.GetSessionAsync(created.Workflow.VideoId, CancellationToken.None);
        Assert.NotNull(session?.TokenHash);
        Assert.DoesNotContain(token, Convert.ToHexString(session!.TokenHash!), StringComparison.Ordinal);
    }

    private static CreateUploadSessionService CreateService(SqliteHarness harness, long maximum,
        FakeUploadProvider? provider = null)
    {
        var policy = TestOptions.Policy(maximum);
        return new CreateUploadSessionService(harness.Store, provider ?? new FakeUploadProvider(),
            new UploadLimitResolver(policy), TestOptions.Pipeline(), policy, Microsoft.Extensions.Options.Options.Create(new S3StorageOptions()),
            TimeProvider.System);
    }
}
