namespace Wishlistarr.Core.MediaServers;

public enum LabelEnforcement
{
    Unknown,
    Enforced,
    NotEnforced,
}

public enum LabelFilterMode
{
    AllowOnly,
}

public sealed record LabelRestriction(LabelEnforcement State, LabelFilterMode? Mode = null, bool Restricted = false)
{
    public static LabelRestriction Unknown { get; } = new(LabelEnforcement.Unknown);
}

public sealed record ProfileRestrictions(LabelRestriction Movies, LabelRestriction Television)
{
    public static ProfileRestrictions Unknown { get; } = new(LabelRestriction.Unknown, LabelRestriction.Unknown);
}

public sealed record UserContentFilters(string? All, string? Movies, string? Television);

public interface IMediaServerRestrictions
{
    Task<IReadOnlyDictionary<string, UserContentFilters>?> GetUserFiltersAsync(CancellationToken ct = default);
}

public static class LabelRestrictionEvaluator
{
    public static ProfileRestrictions Evaluate(UserContentFilters filters, string label) => new(
        Evaluate(filters.Movies, filters.All, label),
        Evaluate(filters.Television, filters.All, label));

    public static LabelRestriction Evaluate(string? specific, string? all, string label)
    {
        LabelRestriction own = Evaluate(specific, label);
        LabelRestriction shared = Evaluate(all, label);
        LabelRestriction result = own.State == LabelEnforcement.Enforced ? own
            : shared.State == LabelEnforcement.Enforced ? shared : own;
        return result with { Restricted = Restricts(specific, all, result) };
    }

    private static bool Restricts(string? specific, string? all, LabelRestriction label)
    {
        bool read = specific is not null || all is not null;
        bool understood = Understood(specific) && Understood(all);
        bool labelAllowed = label.State == LabelEnforcement.Enforced;
        bool ratingCapped = CapsRating(specific) || CapsRating(all);
        return read && understood && (labelAllowed || ratingCapped);
    }

    private static bool Understood(string? filter) =>
        Rules(filter).All(rule => rule.Key is not null
            && (rule.Key.Equals("label", StringComparison.OrdinalIgnoreCase)
                || rule.Key.Equals("contentRating", StringComparison.OrdinalIgnoreCase)));

    private static bool CapsRating(string? filter) =>
        Rules(filter).Any(rule => !rule.Negated
            && rule.Values.Length > 0
            && string.Equals(rule.Key, "contentRating", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string? Key, bool Negated, string[] Values)> Rules(string? filter)
    {
        foreach (string part in (filter ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                yield return (null, false, []);
                continue;
            }

            bool negated = part[equals - 1] == '!';
            yield return (part[..(negated ? equals - 1 : equals)].Trim(), negated, [.. Values(part[(equals + 1)..])]);
        }
    }

    public static LabelRestriction Evaluate(string? filter, string label)
    {
        if (filter is null)
        {
            return LabelRestriction.Unknown;
        }

        bool understood = true;
        bool allowed = false;
        bool excluded = false;
        foreach (string part in filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                understood = false;
                continue;
            }

            bool negated = part[equals - 1] == '!';
            string key = part[..(negated ? equals - 1 : equals)].Trim();
            if (!key.Equals("label", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool matches = Values(part[(equals + 1)..]).Any(value => string.Equals(value, label, StringComparison.OrdinalIgnoreCase));
            allowed |= matches && !negated;
            excluded |= matches && negated;
        }

        if (allowed && !excluded)
        {
            return new LabelRestriction(LabelEnforcement.Enforced, LabelFilterMode.AllowOnly);
        }

        return understood ? new LabelRestriction(LabelEnforcement.NotEnforced) : LabelRestriction.Unknown;
    }

    private static IEnumerable<string> Values(string raw) =>
        Uri.UnescapeDataString(raw).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
