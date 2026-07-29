using System.Threading.Channels;
using Demo.Contracts.Models;
using Demo.UploadApi.Options;
using Microsoft.Extensions.Options;

namespace Demo.UploadApi.Application;

public sealed class UploadCompletedNotificationChannel : IUploadCompletedNotificationPublisher
{
    private readonly Channel<UploadCompletedNotification> _channel;
    private readonly ILogger<UploadCompletedNotificationChannel> _logger;

    public UploadCompletedNotificationChannel(
        IOptions<TranscodeDispatcherOptions> options,
        ILogger<UploadCompletedNotificationChannel> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<UploadCompletedNotification>(new BoundedChannelOptions(options.Value.NotificationCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask PublishAsync(UploadCompletedNotification notification, CancellationToken cancellationToken)
    {
        if (!_channel.Writer.TryWrite(notification))
            _logger.LogWarning("Upload wake-up signal was dropped for {VideoId}; persistent recovery will dispatch it.", notification.VideoId);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<UploadCompletedNotification> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
