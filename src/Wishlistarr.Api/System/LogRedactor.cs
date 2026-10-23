using System.Text.RegularExpressions;

namespace Wishlistarr.Api.System;

public static partial class LogRedactor
{
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "pin",
        "token",
        "secret",
        "apikey",
        "api_key",
        "accesstoken",
        "access_token",
        "authorization",
        "x-api-key",
        "x-plex-token",
    };

    public static bool IsSecretName(string name) => SecretNames.Contains(name);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string redacted = JsonSecret().Replace(text, "$1***$3");
        redacted = HeaderSecret().Replace(redacted, "$1***");
        redacted = BearerSecret().Replace(redacted, "Bearer ***");
        redacted = AssignmentSecret().Replace(redacted, "$1***");
        redacted = WebhookSecret().Replace(redacted, "$1***");
        return redacted;
    }

    [GeneratedRegex("(?i)(\"(?:password|pin|apiKey|api_key|apikey|token|access_token|accessToken|authorization|X-Plex-Token|X-Api-Key)\"\\s*:\\s*\")([^\"\\\\]*)(\")")]
    private static partial Regex JsonSecret();

    [GeneratedRegex("(?i)((?:Authorization|X-Plex-Token|X-Api-Key)\\s*:\\s*(?:Bearer\\s+|Basic\\s+)?)\\S+")]
    private static partial Regex HeaderSecret();

    [GeneratedRegex("(?i)\\bBearer\\s+\\S+")]
    private static partial Regex BearerSecret();

    [GeneratedRegex("(?i)((?:^|[?&\\s{])(?:X-Plex-Token|X-Api-Key|api_key|apikey|apiKey|access_token|accessToken|password|pin|token)\\s*=\\s*)[^\\s&#\"'=]+")]
    private static partial Regex AssignmentSecret();

    [GeneratedRegex("(?i)(/api/v1/webhooks/[a-z]+/)[^\\s/?#\"'&]+")]
    private static partial Regex WebhookSecret();
}
