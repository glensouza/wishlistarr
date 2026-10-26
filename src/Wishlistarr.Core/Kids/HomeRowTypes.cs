using System.Globalization;

namespace Wishlistarr.Core.Kids;

public static class HomeRowTypes
{
    public const string Popular = "popular";
    public const string Library = "library";
    public const string Genre = "genre";
    public const string GenrePrefix = "genre:";

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
        return trimmed.Equals(Popular, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(Library, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(Genre, StringComparison.OrdinalIgnoreCase)
            || TryParseGenre(trimmed, out _);
    }

    public static string Normalize(string row)
    {
        string trimmed = row.Trim();
        return TryParseGenre(trimmed, out int genreId) ? GenreRow(genreId) : trimmed.ToLowerInvariant();
    }

    public static string GenreRow(int genreId) => $"{GenrePrefix}{genreId.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParseGenre(string? row, out int genreId)
    {
        genreId = 0;
        string? trimmed = row?.Trim();
        return trimmed is not null
            && trimmed.StartsWith(GenrePrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed.AsSpan(GenrePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out genreId)
            && genreId > 0;
    }
}
