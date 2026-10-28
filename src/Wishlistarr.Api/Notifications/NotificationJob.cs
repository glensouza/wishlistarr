using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Kids;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public sealed partial class NotificationJob(
    WishlistarrDbContext db,
    NotificationSettings settings,
    NotificationSender sender,
    NotificationLinks links,
    RequestTitles titles,
    WebPushNotices webPush,
    TimeProvider time,
    ILogger<NotificationJob> logger) : IBackgroundJob
{
    public const string JobName = "notifications";
    public const int MaxAttempts = 8;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(60);

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

            MediaRequest? request = null;
            if (delivery.MediaRequestId is int requestId)
            {
                request = await db.MediaRequests.AsNoTracking().Include(row => row.KidProfile)
                    .FirstOrDefaultAsync(row => row.Id == requestId, ct);
                if (delivery.Event == "pending" && request?.State != RequestState.Pending)
                {
                    // Auto-approved, instant and parent-granted requests pass through pending without waiting for anyone.
                    delivery.CompletedAt = now;
                    delivery.LastError = "Skipped because the request was decided before delivery.";
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }

            delivery.Attempts++;
            await db.SaveChangesAsync(ct);
            try
            {
                NotificationMessage message = await this.MessageAsync(delivery, request, subscription.ParentAccountId, ct);
                await sender.SendAsync(subscription.Provider, settings.Read(subscription), message, ct);
                delivery.CompletedAt = time.GetUtcNow();
                delivery.LastError = null;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested && exception is not OperationCanceledException)
            {
                delivery.LastError = exception is NotificationDeliveryException ? exception.Message : "Notification delivery failed.";
                TimeSpan delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, delivery.Attempts - 1)));
                if (exception is NotificationDeliveryException { RetryAfter: TimeSpan retryAfter } && retryAfter > delay)
                {
                    delay = retryAfter < MaxDelay ? retryAfter : MaxDelay;
                }

                delivery.NextAttemptAt = time.GetUtcNow() + delay;
                if (delivery.Attempts >= MaxAttempts)
                {
                    this.LogExhausted(delivery.Id, subscription.Provider, delivery.Attempts);
                }
                else
                {
                    this.LogFailure(delivery.Id, subscription.Provider, delivery.Attempts);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        await webPush.RunAsync(ct);
        DateTimeOffset expired = now - Retention;
        await db.NotificationDeliveries
            .Where(row => (row.CompletedAt != null || row.Attempts >= MaxAttempts) && row.CreatedAt < expired)
            .ExecuteDeleteAsync(ct);
    }

    private async Task<NotificationMessage> MessageAsync(NotificationDelivery delivery, MediaRequest? request, int parentId, CancellationToken ct)
    {
        if (delivery.MediaRequestId is not int requestId)
        {
            return new NotificationMessage(delivery.Message);
        }

        string body = request is null
            ? $"Request #{requestId} changed: {delivery.Event}."
            : Describe(delivery.Event, await titles.GetAsync(request, ct), request.KidProfile?.Name, requestId);
        return delivery.Event == "pending" ? await links.AddAsync(body, parentId, requestId, ct) : new NotificationMessage(body);
    }

    public static string Describe(string kind, string title, string? kid, int requestId)
    {
        string name = title.Length > 0 ? title : $"Request #{requestId}";
        string who = string.IsNullOrEmpty(kid) ? "a kid" : kid;
        return kind switch
        {
            "pending" => $"{who} asked for {name}. It is waiting for your approval.",
            "approved" => $"{name} for {who} was approved.",
            "denied" => $"{name} for {who} was denied.",
            "available" => $"{name} is ready for {who} to watch.",
            "failed" => $"{name} for {who} could not be added. Open Wishlistarr to retry it.",
            _ => $"{name} for {who} changed: {kind}.",
        };
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification delivery {DeliveryId} through {Provider} gave up after {Attempt} attempts")]
    private partial void LogExhausted(int deliveryId, string provider, int attempt);
}

public sealed partial class QueuedParentNotifier(
    WishlistarrDbContext db,
    INotificationTrigger trigger,
    WebPushNotices webPush,
    TimeProvider time,
    ILogger<QueuedParentNotifier> logger) : IParentNotifier
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

        List<NotificationDelivery> queued = [];
        int pushed = 0;
        try
        {
            List<NotificationSubscription> subscriptions = await db.NotificationSubscriptions.Where(row => row.Enabled).ToListAsync(ct);
            foreach (NotificationSubscription subscription in subscriptions.Where(row => row.Events.Split(',').Contains(kind)))
            {
                NotificationDelivery delivery = new()
                {
                    NotificationSubscriptionId = subscription.Id,
                    Event = kind,
                    Message = notification.Message.Length > 1000 ? notification.Message[..1000] : notification.Message,
                    CreatedAt = time.GetUtcNow(),
                    NextAttemptAt = time.GetUtcNow(),
                };
                queued.Add(delivery);
                db.NotificationDeliveries.Add(delivery);
            }

            await db.SaveChangesAsync(ct);
            pushed = await webPush.QueuePlainAsync(kind, notification.Message, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A notification problem must never turn a kid's wrong PIN into a failed sign-in response.
            foreach (NotificationDelivery delivery in queued)
            {
                db.Entry(delivery).State = EntityState.Detached;
            }

            this.LogQueueFailed(exception, notification.Kind);
            return;
        }

        if (queued.Count > 0 || pushed > 0)
        {
            await trigger.StartAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parent notification {Kind} raised")]
    private partial void LogNotification(string kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Parent notification {Kind} could not be queued")]
    private partial void LogQueueFailed(Exception exception, string kind);
}
