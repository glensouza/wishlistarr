using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public sealed record KidReadyNoticeView(int Id, int RequestId, string? Title);

public sealed partial class WebPushNotices(
    WishlistarrDbContext db,
    SettingsStore settings,
    WebPushSender sender,
    NotificationLinks links,
    RequestTitles titles,
    KidRequestFlow flow,
    TimeProvider time,
    ILogger<WebPushNotices> logger)
{
    public const string CursorKey = "system.webpush.cursor";
    public const string ReadyEvent = "ready";
    public const string GenericTitle = "Wishlistarr";
    public const string GenericBody = "Something new is ready to watch.";
    public const int MaxAttempts = 8;

    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(60);
    private static readonly string[] ParentEvents = ["pending", "approved", "denied", "available", "failed"];

    public async Task EnsureCursorAsync(CancellationToken ct = default)
    {
        if (await settings.GetAsync(CursorKey, ct) is not null)
        {
            return;
        }

        int max = await db.MediaRequestEvents.MaxAsync(row => (int?)row.Id, ct) ?? 0;
        await settings.SetAsync(CursorKey, max.ToString(CultureInfo.InvariantCulture), false, ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await this.CollectAsync(ct);
        await this.DeliverAsync(ct);
    }

    public async Task<int> QueuePlainAsync(string kind, string message, CancellationToken ct)
    {
        string text = message.Length > 1000 ? message[..1000] : message;
        List<int> parents = await db.WebPushSubscriptions.AsNoTracking()
            .Where(row => row.ParentAccountId != null)
            .Select(row => row.Id)
            .ToListAsync(ct);
        DateTimeOffset now = time.GetUtcNow();
        foreach (int id in parents)
        {
            db.WebPushDeliveries.Add(new WebPushDelivery
            {
                WebPushSubscriptionId = id,
                Event = kind,
                Message = text,
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }

        if (parents.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return parents.Count;
    }

    public async Task CollectAsync(CancellationToken ct)
    {
        Setting? cursor = await db.Settings.FirstOrDefaultAsync(row => row.Key == CursorKey, ct);
        if (cursor is null || !int.TryParse(cursor.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int position))
        {
            await this.EnsureCursorAsync(ct);
            return;
        }

        List<MediaRequestEvent> events = await db.MediaRequestEvents.AsNoTracking()
            .Where(row => row.Id > position)
            .OrderBy(row => row.Id)
            .Take(500)
            .ToListAsync(ct);
        if (events.Count == 0)
        {
            return;
        }

        int[] requestIds = [.. events.Select(row => row.MediaRequestId).Distinct()];
        Dictionary<int, int> profiles = await db.MediaRequests.AsNoTracking()
            .Where(row => requestIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.KidProfileId, ct);
        List<WebPushSubscription> subscriptions = await db.WebPushSubscriptions.AsNoTracking().ToListAsync(ct);
        int[] deviceIds = [.. subscriptions.Where(row => row.KidDeviceId is not null).Select(row => row.KidDeviceId!.Value)];
        HashSet<int> paired = [.. await db.KidDevices.AsNoTracking()
            .Where(row => deviceIds.Contains(row.Id) && row.TokenHash != null)
            .Select(row => row.Id)
            .ToListAsync(ct)];
        List<KidDeviceProfile> assignments = await db.KidDeviceProfiles.AsNoTracking()
            .Where(row => paired.Contains(row.KidDeviceId))
            .ToListAsync(ct);
        List<WebPushDelivery> existingDeliveries = await db.WebPushDeliveries.AsNoTracking()
            .Where(row => row.MediaRequestId != null && requestIds.Contains(row.MediaRequestId.Value))
            .ToListAsync(ct);
        HashSet<(int Subscription, int Request, string Event)> seen = [];
        foreach (WebPushDelivery delivery in existingDeliveries)
        {
            if (delivery.MediaRequestId is int requestId)
            {
                seen.Add((delivery.WebPushSubscriptionId, requestId, delivery.Event));
            }
        }

        List<KidReadyNotice> existingNotices = await db.KidReadyNotices.AsNoTracking()
            .Where(row => requestIds.Contains(row.MediaRequestId))
            .ToListAsync(ct);
        HashSet<(int Profile, int Request)> notices = [];
        foreach (KidReadyNotice notice in existingNotices)
        {
            notices.Add((notice.KidProfileId, notice.MediaRequestId));
        }
        DateTimeOffset now = time.GetUtcNow();
        foreach (MediaRequestEvent item in events)
        {
            string kind = item.ToState.ToString().ToLowerInvariant();
            if (!profiles.TryGetValue(item.MediaRequestId, out int profileId))
            {
                continue;
            }

            if (ParentEvents.Contains(kind))
            {
                foreach (WebPushSubscription subscription in subscriptions.Where(row => row.ParentAccountId is not null))
                {
                    this.Queue(seen, subscription.Id, item.MediaRequestId, kind, null, now);
                }
            }

            if (item.ToState is not (RequestState.Available or RequestState.Labeled))
            {
                continue;
            }

            if (notices.Add((profileId, item.MediaRequestId)))
            {
                db.KidReadyNotices.Add(new KidReadyNotice
                {
                    KidProfileId = profileId,
                    MediaRequestId = item.MediaRequestId,
                    CreatedAt = now,
                });
            }

            foreach (KidDeviceProfile assignment in assignments.Where(row => row.KidProfileId == profileId))
            {
                WebPushSubscription? subscription = subscriptions.SingleOrDefault(row => row.KidDeviceId == assignment.KidDeviceId);
                if (subscription is not null)
                {
                    this.Queue(seen, subscription.Id, item.MediaRequestId, ReadyEvent, profileId, now);
                }
            }
        }

        cursor.Value = events[^1].Id.ToString(CultureInfo.InvariantCulture);
        cursor.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeliverAsync(CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        List<WebPushDelivery> pending = await db.WebPushDeliveries.Include(row => row.Subscription)
            .Where(row => row.CompletedAt == null && row.Attempts < MaxAttempts && row.NextAttemptAt <= now)
            .OrderBy(row => row.Id)
            .Take(100)
            .ToListAsync(ct);
        foreach (WebPushDelivery delivery in pending)
        {
            WebPushSubscription? subscription = delivery.Subscription;
            if (subscription is null)
            {
                await this.CancelAsync(delivery, "Delivery cancelled because the subscription changed.", ct);
                continue;
            }

            PushText text;
            try
            {
                text = await this.TextAsync(delivery, subscription, ct);
            }
            catch (Exception exception) when (exception is NotificationDeliveryException && !ct.IsCancellationRequested)
            {
                delivery.Attempts++;
                await db.SaveChangesAsync(ct);
                await this.FinishAsync(delivery, subscription, new WebPushResult(WebPushDisposition.Retry, exception.Message, null), ct);
                continue;
            }

            if (text.Skip)
            {
                await this.CancelAsync(delivery, text.Body, ct);
                continue;
            }

            delivery.Attempts++;
            await db.SaveChangesAsync(ct);
            try
            {
                WebPushResult result = await sender.SendAsync(subscription, text.Title, text.Body, ct);
                await this.FinishAsync(delivery, subscription, result, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                string error = exception is NotificationDeliveryException ? exception.Message : "Web push delivery failed.";
                await this.FinishAsync(delivery, subscription, new WebPushResult(WebPushDisposition.Retry, error, null), ct);
            }
        }
    }

    public async Task FinishAsync(WebPushDelivery delivery, WebPushSubscription subscription, WebPushResult result, CancellationToken ct)
    {
        if (result.Disposition == WebPushDisposition.Accepted)
        {
            delivery.CompletedAt = time.GetUtcNow();
            delivery.LastError = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (result.Disposition == WebPushDisposition.Gone)
        {
            int subscriptionId = subscription.Id;
            db.Entry(delivery).State = EntityState.Detached;
            db.Entry(subscription).State = EntityState.Detached;
            await db.WebPushDeliveries.Where(row => row.WebPushSubscriptionId == subscriptionId).ExecuteDeleteAsync(ct);
            await db.WebPushSubscriptions.Where(row => row.Id == subscriptionId).ExecuteDeleteAsync(ct);
            return;
        }

        delivery.LastError = result.Error is { Length: > 200 } error ? error[..200] : result.Error;
        TimeSpan delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, delivery.Attempts - 1))));
        if (result.RetryAfter is TimeSpan retryAfter && retryAfter > delay)
        {
            delay = retryAfter < MaxDelay ? retryAfter : MaxDelay;
        }

        delivery.NextAttemptAt = time.GetUtcNow() + delay;
        await db.SaveChangesAsync(ct);
        if (delivery.Attempts >= MaxAttempts)
        {
            this.LogExhausted(delivery.Id, delivery.Attempts, Status(result.Error));
        }
        else
        {
            this.LogFailure(delivery.Id, delivery.Attempts, Status(result.Error));
        }
    }

    public async Task<IReadOnlyList<KidReadyNoticeView>> ListAsync(int profileId, CancellationToken ct)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstWithContentRulesAsync(row => row.Id == profileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return [];
        }

        List<KidReadyNotice> notices = await db.KidReadyNotices.AsNoTracking()
            .Where(row => row.KidProfileId == profileId && row.DismissedAt == null)
            .OrderByDescending(row => row.Id)
            .ToListAsync(ct);
        int[] requestIds = [.. notices.Select(row => row.MediaRequestId)];
        Dictionary<int, MediaRequest> requests = await db.MediaRequests.AsNoTracking()
            .Where(row => requestIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, ct);
        List<KidReadyNoticeView> views = [];
        foreach (KidReadyNotice notice in notices)
        {
            if (!requests.TryGetValue(notice.MediaRequestId, out MediaRequest? request)
                || request.State is not (RequestState.Available or RequestState.Labeled))
            {
                continue;
            }

            views.Add(new KidReadyNoticeView(notice.Id, notice.MediaRequestId, await this.VisibleNameAsync(profile, request, ct)));
        }

        return views;
    }

    public async Task<bool> DismissAsync(int profileId, int noticeId, CancellationToken ct)
    {
        KidReadyNotice? notice = await db.KidReadyNotices
            .FirstOrDefaultAsync(row => row.Id == noticeId && row.KidProfileId == profileId, ct);
        if (notice is null)
        {
            return false;
        }

        if (notice.DismissedAt is null)
        {
            notice.DismissedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    private void Queue(HashSet<(int Subscription, int Request, string Event)> seen, int subscriptionId, int requestId, string kind, int? profileId, DateTimeOffset now)
    {
        if (!seen.Add((subscriptionId, requestId, kind)))
        {
            return;
        }

        db.WebPushDeliveries.Add(new WebPushDelivery
        {
            WebPushSubscriptionId = subscriptionId,
            MediaRequestId = requestId,
            KidProfileId = profileId,
            Event = kind,
            CreatedAt = now,
            NextAttemptAt = now,
        });
    }

    private async Task<PushText> TextAsync(WebPushDelivery delivery, WebPushSubscription subscription, CancellationToken ct)
    {
        if (subscription.KidDeviceId is int deviceId)
        {
            return await this.KidTextAsync(delivery, deviceId, ct);
        }

        if (subscription.ParentAccountId is null)
        {
            return new PushText(true, string.Empty, "Delivery cancelled because the subscription changed.");
        }

        if (delivery.MediaRequestId is not int requestId)
        {
            return new PushText(false, GenericTitle, string.IsNullOrWhiteSpace(delivery.Message) ? GenericBody : delivery.Message);
        }

        MediaRequest? request = await db.MediaRequests.AsNoTracking().Include(row => row.KidProfile)
            .FirstOrDefaultAsync(row => row.Id == requestId, ct);
        if (delivery.Event == "pending" && request?.State != RequestState.Pending)
        {
            return new PushText(true, string.Empty, "Skipped because the request was decided before delivery.");
        }

        string described = request is null
            ? $"Request #{requestId} changed: {delivery.Event}."
            : NotificationJob.Describe(delivery.Event, await titles.GetAsync(request, ct), request.KidProfile?.Name, requestId);
        if (delivery.Event == "pending")
        {
            described = (await links.AddAsync(described, subscription.ParentAccountId.Value, requestId, ct)).Text;
        }

        return new PushText(false, GenericTitle, described);
    }

    private async Task<PushText> KidTextAsync(WebPushDelivery delivery, int deviceId, CancellationToken ct)
    {
        if (delivery.Event != ReadyEvent || delivery.MediaRequestId is not int requestId || delivery.KidProfileId is not int profileId)
        {
            return new PushText(true, string.Empty, "Delivery cancelled because the device or request changed.");
        }

        bool paired = await db.KidDevices.AnyAsync(row => row.Id == deviceId && row.TokenHash != null, ct);
        bool assigned = await db.KidDeviceProfiles.AnyAsync(row => row.KidDeviceId == deviceId && row.KidProfileId == profileId, ct);
        MediaRequest? request = await db.MediaRequests.AsNoTracking().FirstOrDefaultAsync(row => row.Id == requestId, ct);
        if (!paired || !assigned || request is null || request.KidProfileId != profileId
            || request.State is not (RequestState.Available or RequestState.Labeled))
        {
            return new PushText(true, string.Empty, "Delivery cancelled because the device or request changed.");
        }

        List<int> assignedIds = await db.KidDeviceProfiles.AsNoTracking()
            .Where(row => row.KidDeviceId == deviceId)
            .Select(row => row.KidProfileId)
            .ToListAsync(ct);
        List<KidProfile> profiles = await db.KidProfiles.AsNoTracking()
            .ListWithContentRulesAsync(row => assignedIds.Contains(row.Id) && row.ArchivedAt == null, ct);
        if (profiles.Count != assignedIds.Count || profiles.Count == 0)
        {
            return new PushText(false, GenericTitle, GenericBody);
        }

        string? name = null;
        foreach (KidProfile profile in profiles)
        {
            string? visible = await this.VisibleNameAsync(profile, request, ct);
            if (visible is null)
            {
                return new PushText(false, GenericTitle, GenericBody);
            }

            if (name is null)
            {
                name = visible;
            }
            else if (!string.Equals(name, visible, StringComparison.Ordinal))
            {
                return new PushText(false, GenericTitle, GenericBody);
            }
        }

        return name is null
            ? new PushText(false, GenericTitle, GenericBody)
            : new PushText(false, name, $"{name} is ready to watch.");
    }

    private async Task<string?> VisibleNameAsync(KidProfile profile, MediaRequest request, CancellationToken ct)
    {
        try
        {
            PolicyEvaluation? evaluation = await flow.EvaluateAsync(profile, new TitleKey(request.Kind, request.TmdbId), ct);
            return evaluation?.Decision.Outcome == ContentOutcome.Allowed && evaluation.Name.Length > 0 ? evaluation.Name : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task CancelAsync(WebPushDelivery delivery, string reason, CancellationToken ct)
    {
        delivery.CompletedAt = time.GetUtcNow();
        delivery.LastError = reason.Length > 200 ? reason[..200] : reason;
        await db.SaveChangesAsync(ct);
    }

    private static int Status(string? error)
    {
        if (error is null)
        {
            return 0;
        }

        int start = error.LastIndexOf("HTTP ", StringComparison.Ordinal);
        return start >= 0 && int.TryParse(error.AsSpan(start + 5, Math.Min(3, error.Length - start - 5)), out int status) ? status : 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Web push delivery {DeliveryId} failed on attempt {Attempt} with status {StatusCode}")]
    private partial void LogFailure(int deliveryId, int attempt, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Web push delivery {DeliveryId} gave up after {Attempt} attempts with status {StatusCode}")]
    private partial void LogExhausted(int deliveryId, int attempt, int statusCode);

    private readonly record struct PushText(bool Skip, string Title, string Body);
}
