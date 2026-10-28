namespace Wishlistarr.Integrations.Dtdd;

public sealed class DtddOptions
{
    public const string ApiKeySetting = "dtdd.api_key";

    public Uri ApiBaseAddress { get; set; } = new("https://www.doesthedogdie.com/api/v3/");

    public int MaxRetries { get; set; } = 3;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxTotalRetryDelay { get; set; } = TimeSpan.FromSeconds(4);

    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromDays(7);

    public int MovieTypeId { get; set; } = 15;

    public int TvTypeId { get; set; } = 16;
}
