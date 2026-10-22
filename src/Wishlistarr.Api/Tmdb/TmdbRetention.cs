using System.Globalization;
using Wishlistarr.Api.Settings;

namespace Wishlistarr.Api.Tmdb;

public sealed class TmdbRetention(SettingsStore settings)
{
    public const string SettingKey = "tmdb.retention_days";
    public const int DefaultDays = 90;
    public const int MaxDays = 180;

    public static bool TryParse(string? value, out int days) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out days) && days is >= 1 and <= MaxDays;

    public async Task<TimeSpan> GetAsync(CancellationToken ct = default)
    {
        string? stored = await settings.GetAsync(SettingKey, ct);
        int days = int.TryParse(stored?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= 1
            ? Math.Min(parsed, MaxDays)
            : DefaultDays;
        return TimeSpan.FromDays(days);
    }
}
