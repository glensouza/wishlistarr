using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Wishlistarr.Core.Content;

public static class RatingScale
{
    public const string UsRegion = "US";

    public static IReadOnlyList<string> UsMovieRatings { get; } = ["G", "PG", "PG-13", "R", "NC-17"];

    public static IReadOnlyList<string> UsTvRatings { get; } = ["TV-Y", "TV-G", "TV-Y7", "TV-PG", "TV-14", "TV-MA"];

    private static readonly Dictionary<string, Dictionary<string, RatingLevel>> RegionRatings = new(StringComparer.OrdinalIgnoreCase)
    {
        [UsRegion] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["G"] = RatingLevel.General,
            ["PG"] = RatingLevel.ParentalGuidance,
            ["PG-13"] = RatingLevel.Teen,
            ["R"] = RatingLevel.Mature,
            ["NC-17"] = RatingLevel.Adult,
            ["TV-Y"] = RatingLevel.Preschool,
            ["TV-Y7"] = RatingLevel.Children,
            ["TV-G"] = RatingLevel.General,
            ["TV-PG"] = RatingLevel.ParentalGuidance,
            ["TV-14"] = RatingLevel.Teen,
            ["TV-MA"] = RatingLevel.Mature,
        },
        ["GB"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["U"] = RatingLevel.General,
            ["PG"] = RatingLevel.ParentalGuidance,
        },
        ["AU"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["G"] = RatingLevel.General,
            ["PG"] = RatingLevel.ParentalGuidance,
            ["M"] = RatingLevel.Mature,
        },
        ["CA"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["G"] = RatingLevel.General,
            ["PG"] = RatingLevel.ParentalGuidance,
        },
        ["FR"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["U"] = RatingLevel.General,
        },
    };

    public static int MinimumAge(RatingLevel level) => level switch
    {
        RatingLevel.Preschool or RatingLevel.General => 0,
        RatingLevel.Children => 7,
        RatingLevel.ParentalGuidance => 8,
        RatingLevel.Teen => 13,
        RatingLevel.Mature => 17,
        RatingLevel.Adult => 18,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static RatingLevel MaxLevelForAge(int age) =>
        Enum.GetValues<RatingLevel>().Where(level => MinimumAge(level) <= age).DefaultIfEmpty(RatingLevel.Preschool).Max();

    // Region age ratings are rounded up to the next level, so an imprecise match errs towards stricter.
    public static RatingLevel LevelForMinimumAge(int age)
    {
        RatingLevel[] candidates = [.. Enum.GetValues<RatingLevel>().Where(level => MinimumAge(level) >= age)];
        if (candidates.Length == 0)
        {
            return RatingLevel.Adult;
        }

        int threshold = candidates.Min(MinimumAge);
        return candidates.Where(level => MinimumAge(level) == threshold).Max();
    }

    public static bool TryParse(string? region, string? rating, [NotNullWhen(true)] out RatingLevel? level)
    {
        level = null;
        string code = region?.Trim() ?? string.Empty;
        string value = rating?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return false;
        }

        if (RegionRatings.TryGetValue(code, out Dictionary<string, RatingLevel>? ratings) && ratings.TryGetValue(value, out RatingLevel known))
        {
            level = known;
            return true;
        }

        if (!code.Equals(UsRegion, StringComparison.OrdinalIgnoreCase) && TryReadAge(value, out int age))
        {
            level = LevelForMinimumAge(age);
            return true;
        }

        return false;
    }

    private static bool TryReadAge(string rating, out int age)
    {
        age = 0;
        int start = rating.AsSpan().IndexOfAnyInRange('0', '9');
        if (start < 0)
        {
            return false;
        }

        int length = 0;
        while (start + length < rating.Length && char.IsAsciiDigit(rating[start + length]))
        {
            length++;
        }

        return length <= 2 && int.TryParse(rating.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out age);
    }
}
