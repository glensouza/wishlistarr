namespace Wishlistarr.Api.Webhooks;

public enum WebhookSource
{
    Plex,
    Sonarr,
    Radarr,
    Seerr,
}

public static class WebhookSources
{
    private static readonly Dictionary<string, WebhookSource> ByRoute = Enum.GetValues<WebhookSource>()
        .ToDictionary(source => source.Route(), StringComparer.Ordinal);

    public static string Route(this WebhookSource source) => source.ToString().ToLowerInvariant();

    public static bool TryParse(string? route, out WebhookSource source)
    {
        source = default;
        return route is not null && ByRoute.TryGetValue(route, out source);
    }

    public static string EventTypeProperty(this WebhookSource source) => source switch
    {
        WebhookSource.Plex => "event",
        WebhookSource.Seerr => "notification_type",
        _ => "eventType",
    };
}
