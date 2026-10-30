using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public sealed class RequestArrivalWebhookHandler(WebhookSource source, JobRunner jobs) : IWebhookHandler
{
    public WebhookSource Source => source;

    public async Task HandleAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        bool arrival = this.Source switch
        {
            WebhookSource.Plex => webhookEvent.EventType == "library.new",
            WebhookSource.Sonarr or WebhookSource.Radarr => webhookEvent.EventType == "Download",
            WebhookSource.Seerr => webhookEvent.EventType == "MEDIA_AVAILABLE",
            _ => false,
        };
        if (arrival)
        {
            // Imports can precede Plex's scan, so a webhook wakes the same check that polling will retry later.
            await jobs.StartAsync(RequestArrivalJob.JobName, JobRunTrigger.Webhook, ct, rerunIfRunning: true);
        }
    }
}
