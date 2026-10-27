using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Webhooks;

public enum WebhookIntakeResult
{
    Accepted,
    Duplicate,
}

public sealed class WebhookIntake(
    WishlistarrDbContext db,
    JobRunner runner,
    IOptions<WebhookOptions> options,
    TimeProvider time)
{
    public async Task<WebhookIntakeResult> EnqueueAsync(WebhookSource source, ParsedWebhook webhook, CancellationToken ct = default)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(webhook.Payload)));
        string sourceName = source.Route();
        DateTimeOffset now = time.GetUtcNow();

        List<WebhookEvent> existing = await db.WebhookEvents
            .Where(item => item.Source == sourceName && item.DeliveryKey == key)
            .ToListAsync(ct);
        DateTimeOffset cutoff = now - options.Value.DeduplicationWindow;
        List<WebhookEvent> expired = [.. existing.Where(item => item.ReceivedAt < cutoff && item.ProcessedAt is not null)];
        if (existing.Count > expired.Count)
        {
            return WebhookIntakeResult.Duplicate;
        }

        db.WebhookEvents.RemoveRange(expired);
        db.WebhookEvents.Add(new WebhookEvent
        {
            Source = sourceName,
            DeliveryKey = key,
            EventType = webhook.EventType,
            Payload = webhook.Payload,
            ReceivedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return WebhookIntakeResult.Duplicate;
        }

        await runner.StartAsync(WebhookEventProcessor.JobName, JobRunTrigger.Webhook, CancellationToken.None, rerunIfRunning: true);
        return WebhookIntakeResult.Accepted;
    }
}
