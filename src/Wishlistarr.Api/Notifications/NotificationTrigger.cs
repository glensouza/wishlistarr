using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public interface INotificationTrigger
{
    Task StartAsync();
}

public sealed partial class NotificationTrigger(JobRunner runner, ILogger<NotificationTrigger> logger) : INotificationTrigger
{
    public async Task StartAsync()
    {
        try
        {
            await runner.StartAsync(NotificationJob.JobName, JobRunTrigger.Request, CancellationToken.None, rerunIfRunning: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.LogStartFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start notification delivery")]
    private partial void LogStartFailed(Exception exception);
}
