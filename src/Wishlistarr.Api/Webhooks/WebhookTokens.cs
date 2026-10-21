using System.Security.Cryptography;
using System.Text;
using Wishlistarr.Api.Settings;

namespace Wishlistarr.Api.Webhooks;

public sealed class WebhookTokens(SettingsStore settings)
{
    public const int MaxTokenLength = 128;

    public static string SettingKey(WebhookSource source) => $"system.webhook_token.{source.Route()}";

    public async Task EnsureAllAsync(CancellationToken ct = default)
    {
        foreach (WebhookSource source in Enum.GetValues<WebhookSource>())
        {
            if (await settings.GetAsync(SettingKey(source), ct) is null)
            {
                await this.RegenerateAsync(source, ct);
            }
        }
    }

    public async Task<string> GetAsync(WebhookSource source, CancellationToken ct = default)
    {
        string? token = await settings.GetAsync(SettingKey(source), ct);
        return token ?? await this.RegenerateAsync(source, ct);
    }

    public async Task<string> RegenerateAsync(WebhookSource source, CancellationToken ct = default)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        await settings.SetAsync(SettingKey(source), token, true, ct);
        return token;
    }

    public async Task<bool> IsValidAsync(WebhookSource source, string? candidate, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxTokenLength)
        {
            return false;
        }

        string? expected = await settings.GetAsync(SettingKey(source), ct);
        return expected is not null && CryptographicOperations.FixedTimeEquals(Hash(expected), Hash(candidate));
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
