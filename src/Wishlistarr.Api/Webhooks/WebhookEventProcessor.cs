using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Webhooks;

public interface IWebhookHandler
{
    WebhookSource Source { get; }

    Task HandleAsync(WebhookEvent webhookEvent, CancellationToken ct);
}

public sealed class RecordingWebhookHandler(WebhookSource source, ILogger<RecordingWebhookHandler> logger) : IWebhookHandler
{
    public WebhookSource Source => source;

    public Task HandleAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        logger.LogInformation("Received {Source} webhook event {EventType}.", webhookEvent.Source, webhookEvent.EventType);
        return Task.CompletedTask;
    }
}

public sealed class WebhookEventProcessor(
    WishlistarrDbContext db,
    IEnumerable<IWebhookHandler> handlers,
    IOptions<WebhookOptions> options,
    TimeProvider time,
    ILogger<WebhookEventProcessor> logger) : IBackgroundJob
{
    public const string JobName = "webhook-events";
    private const int BatchSize = 50;

    public async Task RunAsync(CancellationToken ct)
    {
        ILookup<string, IWebhookHandler> bySource = handlers.ToLookup(handler => handler.Source.Route(), StringComparer.Ordinal);
        while (true)
        {
            List<WebhookEvent> pending = await db.WebhookEvents
                .Where(item => item.ProcessedAt == null)
                .OrderBy(item => item.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (pending.Count == 0)
            {
                break;
            }

            foreach (WebhookEvent item in pending)
            {
                try
                {
                    foreach (IWebhookHandler handler in bySource[item.Source])
                    {
                        await handler.HandleAsync(item, ct);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Handling a {Source} webhook event failed.", item.Source);
                }

                item.ProcessedAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(ct);
        }

        await this.RemoveExpiredAsync(ct);
    }

    private async Task RemoveExpiredAsync(CancellationToken ct)
    {
        DateTimeOffset cutoff = time.GetUtcNow() - options.Value.Retention;
        List<WebhookEvent> processed = await db.WebhookEvents.Where(item => item.ProcessedAt != null).ToListAsync(ct);
        db.WebhookEvents.RemoveRange(processed.Where(item => item.ProcessedAt < cutoff));
        await db.SaveChangesAsync(ct);
    }
}
