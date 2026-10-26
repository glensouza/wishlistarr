namespace Wishlistarr.Api.Titles;

public static class YoutubeTrailers
{
    public const string SettingKey = "kids.youtube_trailers";

    public static bool TryParse(string? value, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        return string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEnabled(string? value) => TryParse(value, out bool enabled) && enabled;
}
