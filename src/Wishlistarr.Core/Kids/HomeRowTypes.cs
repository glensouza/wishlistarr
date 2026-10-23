using System.Globalization;

namespace Wishlistarr.Core.Kids;

public static class HomeRowTypes
{
    public const string Popular = "popular";
    public const string Library = "library";
    public const string Genre = "genre";

    public const int AnimationGenreId = 16;
    public const int FamilyGenreId = 10751;
    public const int ComedyGenreId = 35;

    public static readonly IReadOnlyList<string> Defaults = [Popular, Library, Genre];

    public static readonly IReadOnlyList<int> DefaultGenreIds = [AnimationGenreId, FamilyGenreId, ComedyGenreId];

    public static bool IsValid(string? row)
    {
        if (string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        string trimmed = row.Trim();
        if (trimmed.Equals(Popular, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(Library, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(Genre, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.StartsWith("genre:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed.AsSpan("genre:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int genreId)
            && genreId > 0;
    }

    public static string Normalize(string row)
    {
        string trimmed = row.Trim().ToLowerInvariant();
        const string Prefix = "genre:";
        if (trimmed.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(trimmed.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int genreId)
            && genreId > 0)
        {
            return $"{Prefix}{genreId.ToString(CultureInfo.InvariantCulture)}";
        }

        return trimmed;
    }
}
