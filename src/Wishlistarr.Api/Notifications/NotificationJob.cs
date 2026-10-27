using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Core.Kids;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public sealed partial class NotificationJob(
    WishlistarrDbContext db,
    NotificationSettings settings,
    NotificationSender sender,
    NotificationLinks links,
    TimeProvider time,
    ILogger<NotificationJob> logger) : IBackgroundJob
{
    public const string JobName = "notifications";
    public const int MaxAttempts = 8;

    public async Task RunAsync(CancellationToken ct)
    {
        await this.CollectAsync(ct);
        DateTimeOffset now = time.GetUtcNow();
        List<NotificationDelivery> pending = await db.NotificationDeliveries.Include(row => row.Subscription)
            .Where(row => row.CompletedAt == null && row.Attempts < MaxAttempts && row.NextAttemptAt <= now)
            .OrderBy(row => row.Id).Take(500).ToListAsync(ct);
        foreach (NotificationDelivery delivery in pending)
        {
            NotificationSubscription subscription = delivery.Subscription!;
            if (!subscription.Enabled || !subscription.Events.Split(',').Contains(delivery.Event))
            {
                delivery.CompletedAt = now;
                delivery.LastError = "Delivery cancelled because the subscription changed.";
                await db.SaveChangesAsync(ct);
                continue;
            }

            delivery.Attempts++;
            await db.SaveChangesAsync(ct);
            try
            {
                NotificationMessage message = new(delivery.Message);
                if (delivery.MediaRequestId is int requestId)
                {
                    MediaRequest? request = await db.MediaRequests.AsNoTracking().FirstOrDefaultAsync(row => row.Id == requestId, ct);
                    string body = $"{request?.Title ?? $"Request #{requestId}"}: {delivery.Event}.";
                    message = delivery.Event == "pending" && request?.State == RequestState.Pending
                        ? await links.AddAsync(body, subscription.ParentAccountId, requestId, ct)
                        : new NotificationMessage(body);
                }

                await sender.SendAsync(subscription.Provider, settings.Read(subscription), message, ct);
                delivery.CompletedAt = time.GetUtcNow();
                delivery.LastError = null;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested && exception is not OperationCanceledException)
            {
                delivery.LastError = exception is NotificationDeliveryException ? exception.Message : "Notification delivery failed.";
                delivery.NextAttemptAt = time.GetUtcNow() + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, delivery.Attempts - 1)));
                this.LogFailure(delivery.Id, subscription.Provider, delivery.Attempts);
            }

            await db.SaveChangesAsync(ct);
        }

        List<NotificationDelivery> finished = await db.NotificationDeliveries
            .Where(row => row.CompletedAt != null || row.Attempts >= MaxAttempts).ToListAsync(ct);
        List<int> expired = [.. finished.Where(row => row.CreatedAt < now - TimeSpan.FromDays(30)).Select(row => row.Id)];
        await db.NotificationDeliveries.Where(row => expired.Contains(row.Id)).ExecuteDeleteAsync(ct);
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        List<NotificationSubscription> subscriptions = await db.NotificationSubscriptions.ToListAsync(ct);
        foreach (NotificationSubscription subscription in subscriptions)
        {
            List<MediaRequestEvent> events = await db.MediaRequestEvents.AsNoTracking()
                .Where(row => row.Id > subscription.LastEventId).OrderBy(row => row.Id).Take(500).ToListAsync(ct);
            foreach (MediaRequestEvent item in events)
            {
                string kind = item.ToState.ToString().ToLowerInvariant();
                if (subscription.Enabled && subscription.Events.Split(',').Contains(kind))
                {
                    db.NotificationDeliveries.Add(new NotificationDelivery
                    {
                        NotificationSubscriptionId = subscription.Id,
                        MediaRequestEventId = item.Id,
                        MediaRequestId = item.MediaRequestId,
                        Event = kind,
                        Message = $"Request #{item.MediaRequestId}: {kind}.",
                        CreatedAt = time.GetUtcNow(),
                        NextAttemptAt = time.GetUtcNow(),
                    });
                }

                subscription.LastEventId = item.Id;
            }

            // The cursor and its deliveries commit together, so restarts cannot skip events.
            await db.SaveChangesAsync(ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification delivery {DeliveryId} through {Provider} failed on attempt {Attempt}")]
    private partial void LogFailure(int deliveryId, string provider, int attempt);
}

public sealed partial class QueuedParentNotifier(WishlistarrDbContext db, TimeProvider time, ILogger<QueuedParentNotifier> logger) : IParentNotifier
{
    public async Task NotifyAsync(ParentNotification notification, CancellationToken ct = default)
    {
        string? kind = notification.Kind switch
        {
            ParentNotificationKinds.PinFailures => "pinFailures",
            ParentNotificationKinds.ProfileLocked => "profileLocked",
            _ => null,
        };
        this.LogNotification(notification.Kind);
        if (kind is null)
        {
            return;
        }

        List<NotificationSubscription> subscriptions = await db.NotificationSubscriptions.Where(row => row.Enabled).ToListAsync(ct);
        foreach (NotificationSubscription subscription in subscriptions.Where(row => row.Events.Split(',').Contains(kind)))
        {
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                NotificationSubscriptionId = subscription.Id,
                Event = kind,
                Message = notification.Message.Length > 1000 ? notification.Message[..1000] : notification.Message,
                CreatedAt = time.GetUtcNow(),
                NextAttemptAt = time.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parent notification {Kind} queued for configured recipients")]
    private partial void LogNotification(string kind);
}
