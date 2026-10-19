using System.Security.Cryptography;
using System.Text;
using Wishlistarr.Api.Settings;

namespace Wishlistarr.Api.Security;

public sealed class ApiKeyService
{
    public const string HeaderName = "X-Api-Key";
    public const string SettingKey = "system.api_key";

    private volatile byte[]? keyHash;

    public async Task EnsureAsync(SettingsStore settings, CancellationToken ct = default)
    {
        string? key = await settings.GetAsync(SettingKey, ct);
        if (key is null)
        {
            key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            await settings.SetAsync(SettingKey, key, true, ct);
            Console.Out.WriteLine($"Generated API key: {key}");
        }

        this.keyHash = Hash(key);
    }

    public bool IsValid(string? candidate)
    {
        byte[]? expected = this.keyHash;
        return expected is not null
            && !string.IsNullOrEmpty(candidate)
            && CryptographicOperations.FixedTimeEquals(expected, Hash(candidate));
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
