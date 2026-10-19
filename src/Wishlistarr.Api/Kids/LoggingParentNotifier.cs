using Wishlistarr.Core.Kids;

namespace Wishlistarr.Api.Kids;

public sealed partial class LoggingParentNotifier(ILogger<LoggingParentNotifier> logger) : IParentNotifier
{
    public Task NotifyAsync(ParentNotification notification, CancellationToken ct = default)
    {
        this.LogNotification(notification.Kind, notification.Message);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parent notification {Kind}: {Message}")]
    private partial void LogNotification(string kind, string message);
}
