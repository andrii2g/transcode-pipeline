using Demo.Contracts.Enums;
using Demo.UploadApi.Application;
using Demo.UploadApi.Infrastructure.Uploads;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Tests;

public sealed class LocalUploadServiceTests
{
    [Fact]
    public async Task Exact_maximum_is_published_atomically_and_signaled_after_persistence()
    {
        await using var context = await CreateAsync(1024, 1024);
        await context.Service.UploadAsync(context.VideoId, context.Token, new MemoryStream(new byte[1024]),
            null, "video/mp4", CancellationToken.None);
        var workflow = await context.Harness.Store.GetAsync(context.VideoId, CancellationToken.None);
        Assert.Equal(TranscodeJobStatus.Uploaded, workflow?.Status);
        Assert.Equal(1024, workflow?.ActualSizeBytes);
        Assert.True(File.Exists(context.Paths.SourcePath(workflow!.Source.LocalRelativePath!)));
        Assert.False(File.Exists(context.Paths.TemporaryUploadPath(context.VideoId)));
        await using var reader = context.Channel.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(context.VideoId, reader.Current.VideoId);
    }

    [Fact]
    public async Task Content_length_over_limit_is_rejected_before_body_read()
    {
        await using var context = await CreateAsync(10, 10);
        var body = new CountingStream(new byte[1]);
        var error = await Assert.ThrowsAsync<LocalUploadException>(() => context.Service.UploadAsync(
            context.VideoId, context.Token, body, 11, "video/mp4", CancellationToken.None));
        Assert.Equal("FileSizeExceeded", error.Code);
        Assert.Equal(0, body.Reads);
        Assert.False(File.Exists(context.Paths.TemporaryUploadPath(context.VideoId)));
        Assert.Equal(TranscodeJobStatus.UploadRejected,
            (await context.Harness.Store.GetAsync(context.VideoId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task Chunked_body_stops_on_first_buffer_over_limit_and_never_publishes()
    {
        await using var context = await CreateAsync(1024, 1024);
        var body = new CountingStream(new byte[1025]);
        var error = await Assert.ThrowsAsync<LocalUploadException>(() => context.Service.UploadAsync(
            context.VideoId, context.Token, body, null, "video/mp4", CancellationToken.None));
        Assert.Equal("FileSizeExceeded", error.Code);
        Assert.Equal(1, body.Reads);
        var workflow = await context.Harness.Store.GetAsync(context.VideoId, CancellationToken.None);
        Assert.False(File.Exists(context.Paths.SourcePath(workflow!.Source.LocalRelativePath!)));
        Assert.False(File.Exists(context.Paths.TemporaryUploadPath(context.VideoId)));
    }

    [Fact]
    public async Task Invalid_token_does_not_consume_or_reject_session()
    {
        await using var context = await CreateAsync(10, 10);
        var error = await Assert.ThrowsAsync<LocalUploadException>(() => context.Service.UploadAsync(
            context.VideoId, "invalid", new MemoryStream(new byte[10]), 10, "video/mp4", CancellationToken.None));
        Assert.Equal("UploadTokenInvalid", error.Code);
        Assert.Equal(TranscodeJobStatus.UploadPending,
            (await context.Harness.Store.GetAsync(context.VideoId, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task Reused_token_is_rejected()
    {
        await using var context = await CreateAsync(10, 10);
        await context.Service.UploadAsync(context.VideoId, context.Token, new MemoryStream(new byte[10]),
            10, "video/mp4", CancellationToken.None);
        var error = await Assert.ThrowsAsync<LocalUploadException>(() => context.Service.UploadAsync(
            context.VideoId, context.Token, new MemoryStream(new byte[10]), 10, "video/mp4", CancellationToken.None));
        Assert.Equal("UploadAlreadyUsed", error.Code);
    }

    [Fact]
    public async Task Declared_size_mismatch_and_disconnect_clean_temporary_file()
    {
        await using var mismatch = await CreateAsync(10, 20);
        var error = await Assert.ThrowsAsync<LocalUploadException>(() => mismatch.Service.UploadAsync(
            mismatch.VideoId, mismatch.Token, new MemoryStream(new byte[9]), null, "video/mp4", CancellationToken.None));
        Assert.Equal("DeclaredSizeMismatch", error.Code);
        Assert.False(File.Exists(mismatch.Paths.TemporaryUploadPath(mismatch.VideoId)));

        await using var disconnected = await CreateAsync(10, 20);
        await Assert.ThrowsAsync<OperationCanceledException>(() => disconnected.Service.UploadAsync(
            disconnected.VideoId, disconnected.Token, new DisconnectingStream(), null, "video/mp4", CancellationToken.None));
        Assert.False(File.Exists(disconnected.Paths.TemporaryUploadPath(disconnected.VideoId)));
        var interrupted = await disconnected.Harness.Store.GetAsync(disconnected.VideoId, CancellationToken.None);
        Assert.Equal(TranscodeJobStatus.UploadRejected, interrupted?.Status);
        Assert.Equal("UploadCanceled", interrupted?.ErrorCode);
    }

    private static async Task<LocalContext> CreateAsync(long declared, long maximum)
    {
        var harness = new SqliteHarness();
        await harness.InitializeAsync();
        var provider = new FakeUploadProvider();
        var policy = TestOptions.Policy(maximum);
        var create = new CreateUploadSessionService(harness.Store, provider, new UploadLimitResolver(policy),
            TestOptions.Pipeline(), policy, Microsoft.Extensions.Options.Options.Create(new S3StorageOptions()), TimeProvider.System);
        var created = await create.CreateAsync("video.mp4", "video/mp4", declared, null, CancellationToken.None);
        var localOptions = Microsoft.Extensions.Options.Options.Create(new LocalStorageOptions
        {
            PublicBaseUrl = "https://example.test",
            TemporaryUploadDirectory = Path.Combine(harness.Root, "temp", "uploads"),
            SourceDirectory = Path.Combine(harness.Root, "source"),
            TemporaryOutputDirectory = Path.Combine(harness.Root, "temp", "outputs"),
            OutputDirectory = Path.Combine(harness.Root, "output"),
            MaximumConcurrentUploads = 2,
            RequireDeclaredSizeMatch = true
        });
        var paths = new LocalPathResolver(localOptions);
        var channel = TestOptions.Channel();
        var service = new LocalUploadService(harness.Store, channel, paths, localOptions, TimeProvider.System);
        return new LocalContext(harness, paths, channel, service, created.Workflow.VideoId,
            created.Instruction.Headers["X-Upload-Token"]);
    }

    private sealed record LocalContext(SqliteHarness Harness, LocalPathResolver Paths,
        UploadCompletedNotificationChannel Channel, LocalUploadService Service, string VideoId, string Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Service.Dispose(); await Harness.DisposeAsync(); }
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    }

    private sealed class DisconnectingStream : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new OperationCanceledException("Disconnected"));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
