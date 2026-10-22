using System.Text.Json;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Library;

public sealed class LibraryWebhookHandler(LibraryRefresh refresh, JobRunner jobs, ILogger<LibraryWebhookHandler> logger) : IWebhookHandler
{
    private const string LibraryNewEvent = "library.new";

    public WebhookSource Source => WebhookSource.Plex;

    public async Task HandleAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        if (webhookEvent.EventType != LibraryNewEvent)
        {
            return;
        }

        string? ratingKey = ReadRatingKey(webhookEvent.Payload);
        try
        {
            if (ratingKey is not null && await this.TryRefreshItemAsync(ratingKey, ct))
            {
                return;
            }

            JobStartResult started = await jobs.StartAsync(LibraryIncrementalRefreshJob.JobName, JobRunTrigger.Webhook, ct);
            switch (started.Status)
            {
                case JobStartStatus.AlreadyRunning:
                    logger.LogInformation("An incremental library refresh is already running; a {Event} event relies on it or the next one.", webhookEvent.EventType);
                    break;
                case JobStartStatus.NotFound or JobStartStatus.Stopping:
                    logger.LogWarning("The library refresh could not be started: {Status}.", started.Status);
                    break;
            }
        }
        catch (MediaServerNotConfiguredException exception)
        {
            logger.LogDebug("Library webhook skipped: {Reason}", exception.Message);
        }
    }

    private async Task<bool> TryRefreshItemAsync(string ratingKey, CancellationToken ct)
    {
        try
        {
            return await refresh.TryItemAsync(ratingKey, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is MediaServerException and not MediaServerNotConfiguredException)
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Refreshing a single library item failed; falling back to an incremental refresh.");
            return false;
        }
    }

    private static string? ReadRatingKey(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Metadata", out JsonElement metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("ratingKey", out JsonElement key)
                && key.ValueKind == JsonValueKind.String
                && key.GetString() is { Length: > 0 and <= 20 } value
                && value.All(char.IsAsciiLetterOrDigit))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // A malformed payload falls back to the incremental refresh.
        }

        return null;
    }
}
