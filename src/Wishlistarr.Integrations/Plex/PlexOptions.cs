namespace Wishlistarr.Integrations.Plex;

public sealed class PlexOptions
{
    public Uri PlexTvBaseAddress { get; set; } = new("https://plex.tv/");

    public Uri AuthAppAddress { get; set; } = new("https://app.plex.tv/auth");

    public string Product { get; set; } = "Wishlistarr";

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan LookupCacheLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public int PageSize { get; set; } = 500;
}
