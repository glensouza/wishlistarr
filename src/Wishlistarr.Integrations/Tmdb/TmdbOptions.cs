namespace Wishlistarr.Integrations.Tmdb;

public sealed class TmdbOptions
{
    public const string ApiKeySetting = "tmdb.api_key";

    public Uri ApiBaseAddress { get; set; } = new("https://api.themoviedb.org/3/");

    public Uri ImageBaseAddress { get; set; } = new("https://image.tmdb.org/t/p/");

    public int MaxRetries { get; set; } = 3;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxTotalRetryDelay { get; set; } = TimeSpan.FromSeconds(45);

    public int CacheMaxEntries { get; set; } = 2000;
}
