using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Wishlistarr.Api.System;

public static partial class LogRedactor
{
    public const string Mask = "***";

    private const string NameChars = "[A-Za-z0-9_.\\-]";
    private const string SecretName = "(?:" + NameChars + "*(?:token|key|password|secret|cookie|authorization|auth)" + NameChars + "*|" + NameChars + "*pin)";

    private static readonly string[] ContainedNames = ["token", "key", "password", "secret", "cookie", "authorization", "auth"];

    public static bool IsSecretName(string name)
    {
        if (name.EndsWith("pin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string part in ContainedNames)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string redacted = JsonSecret().Replace(text, "$1***$3");
        redacted = JsonBareSecret().Replace(redacted, "$1***");
        redacted = CookieHeader().Replace(redacted, "$1***");
        redacted = HeaderSecret().Replace(redacted, "$1***");
        redacted = BearerSecret().Replace(redacted, "Bearer ***");
        redacted = SessionCookie().Replace(redacted, "$1***");
        redacted = AssignmentSecret().Replace(redacted, "$1***");
        redacted = WebhookSecret().Replace(redacted, "$1***");
        return redacted;
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

    [GeneratedRegex("(?i)(\"" + SecretName + "\"\\s*:\\s*\")([^\"\\\\]*)(\")")]
    private static partial Regex JsonSecret();

    [GeneratedRegex("(?i)(\"" + SecretName + "\"\\s*:\\s*)(?:-?\\d[\\w.+\\-]*|true|false)")]
    private static partial Regex JsonBareSecret();

    [GeneratedRegex("(?i)(\\b(?:Set-)?Cookie\\s*:\\s*)[^\\r\\n]+")]
    private static partial Regex CookieHeader();

    [GeneratedRegex("(?i)((?:Proxy-)?(?:Authorization|X-Plex-Token|X-Api-Key)\\s*:\\s*(?:Bearer\\s+|Basic\\s+)?)\\S+")]
    private static partial Regex HeaderSecret();

    [GeneratedRegex("(?i)\\bBearer\\s+\\S+")]
    private static partial Regex BearerSecret();

    [GeneratedRegex("(?i)(\\bwishlistarr\\.[a-z]+\\s*=\\s*)[^;\\s,\"']+")]
    private static partial Regex SessionCookie();

    [GeneratedRegex("(?i)((?:^|[?&\\s{,;(])" + SecretName + "\\s*=\\s*[\"']?)[^\\s&#\"'=;,)]+")]
    private static partial Regex AssignmentSecret();

    [GeneratedRegex("(?i)(/api/v1/webhooks/[a-z]+/)[^\\s/?#\"'&]+")]
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
