using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Wishlistarr.Api.System;

public static partial class LogRedactor
{
    public const string Mask = "***";

    private const string NameChars = "[A-Za-z0-9_.\\-]";
    private const int MatchTimeoutMilliseconds = 500;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;

    private static readonly string[] ContainedNames = ["token", "secret", "password", "passwd", "cookie", "authorization", "oauth", "apikey", "privatekey"];

    private static readonly string[] SecretWords = ["key", "auth", "pin"];

    private static readonly HashSet<string> HarmlessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CancellationToken",
        "CacheKey",
    };

    public static bool IsSecretName(string name)
    {
        string flat = string.Concat(name.Where(char.IsLetterOrDigit));
        if (flat.Length == 0 || HarmlessNames.Contains(flat))
        {
            return false;
        }

        foreach (string part in ContainedNames)
        {
            if (flat.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (flat.EndsWith("pin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Words(name).Any(word => SecretWords.Contains(word, StringComparer.OrdinalIgnoreCase));
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return FailClosed(() => RedactText(text));
    }

    internal static string FailClosed(Func<string> redact)
    {
        try
        {
            return redact();
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }
    }

    private static string RedactText(string text)
    {
        string redacted = JsonSecret().Replace(text, match => IsSecretName(match.Groups["n"].Value)
            ? match.Groups["p"].Value + Mask + match.Groups["c"].Value
            : match.Value);
        redacted = JsonBareSecret().Replace(redacted, MaskSecretValue);
        redacted = CookieHeader().Replace(redacted, "$1***");
        redacted = HeaderSecret().Replace(redacted, "$1***");
        redacted = BearerSecret().Replace(redacted, "Bearer ***");
        redacted = SessionCookie().Replace(redacted, "$1***");
        redacted = XmlSecret().Replace(redacted, MaskSecretValue);
        redacted = AssignmentSecret().Replace(redacted, MaskSecretValue);
        redacted = ColonSecret().Replace(redacted, MaskSecretValue);
        redacted = WebhookSecret().Replace(redacted, "$1***");
        return redacted;
    }

    private static string MaskSecretValue(Match match) =>
        IsSecretName(match.Groups["n"].Value) ? match.Groups["p"].Value + Mask : match.Value;

    private static List<string> Words(string name)
    {
        List<string> words = [];
        int start = -1;
        for (int index = 0; index <= name.Length; index++)
        {
            bool inWord = index < name.Length && char.IsLetterOrDigit(name[index]);
            bool split = start >= 0 && (!inWord || StartsNewWord(name, index));
            if (split)
            {
                words.Add(name[start..index]);
                start = -1;
            }

            if (inWord && start < 0)
            {
                start = index;
            }
        }

        return words;
    }

    private static bool StartsNewWord(string name, int index)
    {
        if (!char.IsUpper(name[index]))
        {
            return false;
        }

        char previous = name[index - 1];
        return char.IsLower(previous)
            || char.IsDigit(previous)
            || (char.IsUpper(previous) && index + 1 < name.Length && char.IsLower(name[index + 1]));
    }

    public static LogEventPropertyValue Redact(string? name, LogEventPropertyValue value)
    {
        if (name is not null && IsSecretName(name) && value is not ScalarValue { Value: null })
        {
            return new ScalarValue(Mask);
        }

        return value switch
        {
            ScalarValue scalar => RedactScalar(scalar),
            StructureValue structure => RedactStructure(structure),
            SequenceValue sequence => RedactSequence(sequence),
            DictionaryValue dictionary => RedactDictionary(dictionary),
            _ => value,
        };
    }

    private static LogEventPropertyValue RedactScalar(ScalarValue scalar)
    {
        string? text = scalar.Value switch
        {
            null => null,
            string value => value,
            Enum or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                or Guid or DateTime or DateTimeOffset or TimeSpan => null,
            object other => other.ToString(),
        };
        if (text is null)
        {
            return scalar;
        }

        string redacted = Redact(text);
        return string.Equals(redacted, text, StringComparison.Ordinal) ? scalar : new ScalarValue(redacted);
    }

    private static StructureValue RedactStructure(StructureValue structure) =>
        new(
            [.. structure.Properties.Select(property => new LogEventProperty(property.Name, Redact(property.Name, property.Value)))],
            structure.TypeTag);

    private static SequenceValue RedactSequence(SequenceValue sequence) =>
        new(sequence.Elements.Select(element => Redact(null, element)));

    private static DictionaryValue RedactDictionary(DictionaryValue dictionary) =>
        new(dictionary.Elements.Select(pair =>
        {
            string? key = pair.Key.Value as string;
            ScalarValue safeKey = key is null ? pair.Key : new ScalarValue(Redact(key));
            return new KeyValuePair<ScalarValue, LogEventPropertyValue>(safeKey, Redact(key, pair.Value));
        }));

    [GeneratedRegex("(?<p>\"(?<n>(?>" + NameChars + "+))\"\\s*:\\s*\")(?:[^\"\\\\]|\\\\[\\s\\S]?)*(?<c>\"?)", Options, MatchTimeoutMilliseconds)]
    private static partial Regex JsonSecret();

    [GeneratedRegex("(?<p>\"(?<n>(?>" + NameChars + "+))\"\\s*:\\s*)(?:-?\\d[\\w.+\\-]*|true|false)", Options, MatchTimeoutMilliseconds)]
    private static partial Regex JsonBareSecret();

    [GeneratedRegex("(\\b(?:Set-)?Cookie\\s*:\\s*)[^\\r\\n]+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex CookieHeader();

    [GeneratedRegex("((?:Proxy-)?(?:Authorization|X-Plex-Token|X-Api-Key)\\s*:\\s*(?:Bearer\\s+|Basic\\s+)?)\\S+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex HeaderSecret();

    [GeneratedRegex("\\bBearer\\s+\\S+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex BearerSecret();

    [GeneratedRegex("(\\bwishlistarr\\.[a-z]+\\s*=\\s*)[^;\\s,\"']+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex SessionCookie();

    [GeneratedRegex("(?<p><(?<n>" + NameChars + "+)(?:\\s[^<>]*)?>)[^<>]+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex XmlSecret();

    [GeneratedRegex("(?<p>(?:^|[?&\\s{,;(])(?<n>(?>" + NameChars + "+))\\s*=\\s*[\"']?)[^\\s&#\"'=;,)]+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex AssignmentSecret();

    [GeneratedRegex("(?<p>(?:^|[\\s{,;(])(?<n>(?>" + NameChars + "+))\\s*:\\s*[\"']?)[^\\s,;\"'<>)}\\]]+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex ColonSecret();

    [GeneratedRegex("(/api/v1/webhooks/[a-z]+/)[^\\s/?#\"'&]+", Options, MatchTimeoutMilliseconds)]
    private static partial Regex WebhookSecret();
}

public sealed class LogEventRedactor : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties.ToArray())
        {
            LogEventPropertyValue redacted = LogRedactor.Redact(property.Key, property.Value);
            if (!ReferenceEquals(redacted, property.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, redacted));
            }
        }
    }
}
