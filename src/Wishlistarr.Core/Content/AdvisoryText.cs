using System.Collections.Immutable;

namespace Wishlistarr.Core.Content;

internal static class AdvisoryText
{
    public static string? Canonical(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static ImmutableHashSet<string> ToSet(IEnumerable<string>? values)
    {
        ImmutableHashSet<string>.Builder builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return builder.ToImmutable();
        }

        foreach (string value in values)
        {
            string? text = Canonical(value);
            if (text is not null)
            {
                builder.Add(text);
            }
        }

        return builder.ToImmutable();
    }

    public static string? FirstMatch(IReadOnlyList<string>? values, IReadOnlySet<string> blocked)
    {
        if (values is null || blocked.Count == 0)
        {
            return null;
        }

        foreach (string value in values)
        {
            string? text = Canonical(value);
            if (text is not null && blocked.Contains(text))
            {
                return text;
            }
        }

        return null;
    }
}
