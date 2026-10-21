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
            if (ratingKey is null)
            {
                await jobs.StartAsync(LibraryIncrementalRefreshJob.JobName, JobRunTrigger.Webhook, ct);
            }
            else
            {
                await refresh.ItemAsync(ratingKey, ct);
            }
        }
        catch (MediaServerNotConfiguredException exception)
        {
            logger.LogDebug("Library webhook skipped: {Reason}", exception.Message);
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
        }

        return null;
    }
}
