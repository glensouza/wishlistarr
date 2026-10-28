using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wishlistarr.Api.Settings;

namespace Wishlistarr.Api.Notifications;

public sealed class VapidKeys(SettingsStore settings, TimeProvider time)
{
    public const string SettingKey = "system.webpush.vapid";
    public const string Subject = "mailto:wishlistarr@localhost";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<string> PublicKeyAsync(CancellationToken ct = default)
    {
        byte[] material = await this.MaterialAsync(ct);
        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(material, out _);
            return Base64Url.EncodeToString(WebPushEncryption.Uncompressed(ecdsa.ExportParameters(false).Q));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public async Task<string> CreateAuthorizationAsync(Uri endpoint, CancellationToken ct = default)
    {
        byte[] material = await this.MaterialAsync(ct);
        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(material, out _);
            string publicKey = Base64Url.EncodeToString(WebPushEncryption.Uncompressed(ecdsa.ExportParameters(false).Q));
            long exp = time.GetUtcNow().ToUnixTimeSeconds() + (12 * 60 * 60);
            string claims = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
                ["exp"] = exp,
                ["sub"] = Subject,
            });
            string encodedHeader = Base64Url.EncodeToString("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
            string encodedClaims = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims));
            byte[] input = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{encodedHeader}.{encodedClaims}"));
            byte[] signature = ecdsa.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"vapid t={encodedHeader}.{encodedClaims}.{Base64Url.EncodeToString(signature)},k={publicKey}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private async Task<byte[]> MaterialAsync(CancellationToken ct)
    {
        byte[]? existing = await this.ReadAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        await Gate.WaitAsync(ct);
        try
        {
            existing = await this.ReadAsync(ct);
            if (existing is not null)
            {
                return existing;
            }

            using ECDsa created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] exported = created.ExportPkcs8PrivateKey();
            await settings.SetAsync(SettingKey, Base64Url.EncodeToString(exported), true, ct);
            return exported;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        string? stored = await settings.GetAsync(SettingKey, ct);
        if (stored is null)
        {
            return null;
        }

        try
        {
            return Base64Url.DecodeFromChars(stored);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The saved web push signing key could not be read.");
        }
    }
}
