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

public sealed record LabelRestriction(LabelEnforcement State, LabelFilterMode? Mode = null)
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
        if (own.State == LabelEnforcement.Enforced)
        {
            return own;
        }

        LabelRestriction shared = Evaluate(all, label);
        return shared.State == LabelEnforcement.Enforced ? shared : own;
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
