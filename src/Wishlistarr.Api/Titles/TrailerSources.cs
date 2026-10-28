namespace Wishlistarr.Api.Titles;

public static class TrailerSources
{
    public const string SettingKey = "kids.trailer_sources";

    public static bool TryParse(string? value, out string[] sources)
    {
        sources = [];
        if (value is null) return false;
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts is ["none"]) return true;
        if (parts.Length is < 1 or > 2 || parts.Any(part => part is not ("plex" or "youtube")) || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length) return false;
        sources = parts;
        return true;
    }
}
