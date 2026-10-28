using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Wishlistarr.Api.Notifications;

public static class WebPushEncryption
{
    public const int RecordSize = 4096;
    public const int AuthLength = 16;
    public const int PublicKeyLength = 65;

    // RFC 8291 appends an explicit NUL after each HKDF info label. It is a separator, not a C-string terminator.
    private static ReadOnlySpan<byte> ContentKeyInfo => "Content-Encoding: aes128gcm\0"u8;

    private static ReadOnlySpan<byte> NonceInfo => "Content-Encoding: nonce\0"u8;

    public static byte[] Seal(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> userAgentPublic,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> salt = default,
        ECDiffieHellman? ephemeral = null)
    {
        if (authSecret.Length != AuthLength)
        {
            throw new ArgumentException("The authentication secret must be 16 octets.", nameof(authSecret));
        }

        if (!TryPublic(userAgentPublic, out ECParameters userAgent))
        {
            throw new ArgumentException("The user agent public key is not a P-256 point.", nameof(userAgentPublic));
        }

        if (plaintext.Length + 1 + 16 >= RecordSize)
        {
            throw new ArgumentException("The push message does not fit in one record.", nameof(plaintext));
        }

        bool owned = ephemeral is null;
        ephemeral ??= ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            byte[] serverPublic = Uncompressed(ephemeral.ExportParameters(false).Q);
            byte[] saltBytes = salt.IsEmpty ? RandomNumberGenerator.GetBytes(16) : salt.ToArray();
            if (saltBytes.Length != 16)
            {
                throw new ArgumentException("The salt must be 16 octets.", nameof(salt));
            }

            using ECDiffieHellman peer = ECDiffieHellman.Create(userAgent);
            byte[] secret = Pad32(ephemeral.DeriveRawSecretAgreement(peer.PublicKey));
            byte[] keyInfo = Combine("WebPush: info"u8, userAgentPublic, serverPublic);
            byte[] ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, authSecret.ToArray(), keyInfo);
            byte[] contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, saltBytes, ContentKeyInfo.ToArray());
            byte[] nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, saltBytes, NonceInfo.ToArray());
            byte[] record = new byte[plaintext.Length + 1];
            plaintext.CopyTo(record);
            record[^1] = 2;
            byte[] cipher = new byte[record.Length];
            byte[] tag = new byte[16];
            using AesGcm aes = new(contentKey, 16);
            aes.Encrypt(nonce, record, cipher, tag);
            byte[] body = new byte[86 + cipher.Length + tag.Length];
            saltBytes.CopyTo(body);
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(16, 4), RecordSize);
            body[20] = PublicKeyLength;
            serverPublic.CopyTo(body.AsSpan(21));
            cipher.CopyTo(body.AsSpan(86));
            tag.CopyTo(body.AsSpan(86 + cipher.Length));
            return body;
        }
        finally
        {
            if (owned)
            {
                ephemeral.Dispose();
            }
        }
    }

    public static byte[] Uncompressed(ECPoint point)
    {
        byte[] encoded = new byte[PublicKeyLength];
        encoded[0] = 4;
        CopyCoordinate(point.X, encoded.AsSpan(1, 32));
        CopyCoordinate(point.Y, encoded.AsSpan(33, 32));
        return encoded;
    }

    public static bool TryPublic(ReadOnlySpan<byte> uncompressed, out ECParameters parameters)
    {
        parameters = default;
        if (uncompressed.Length != PublicKeyLength || uncompressed[0] != 4)
        {
            return false;
        }

        try
        {
            parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = uncompressed.Slice(1, 32).ToArray(),
                    Y = uncompressed.Slice(33, 32).ToArray(),
                },
            };
            using ECDiffieHellman probe = ECDiffieHellman.Create(parameters);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Pad32(byte[] secret)
    {
        if (secret.Length == 32)
        {
            return secret;
        }

        byte[] padded = new byte[32];
        if (secret.Length > 32)
        {
            secret.AsSpan(secret.Length - 32).CopyTo(padded);
        }
        else
        {
            secret.CopyTo(padded.AsSpan(32 - secret.Length));
        }

        return padded;
    }

    private static void CopyCoordinate(byte[]? coordinate, Span<byte> destination)
    {
        if (coordinate is not { Length: > 0 and <= 32 })
        {
            throw new CryptographicException("The P-256 coordinate is missing.");
        }

        coordinate.CopyTo(destination[(32 - coordinate.Length)..]);
    }

    private static byte[] Combine(ReadOnlySpan<byte> label, ReadOnlySpan<byte> userAgent, ReadOnlySpan<byte> server)
    {
        byte[] info = new byte[label.Length + 1 + userAgent.Length + server.Length];
        label.CopyTo(info);
        userAgent.CopyTo(info.AsSpan(label.Length + 1));
        server.CopyTo(info.AsSpan(label.Length + 1 + userAgent.Length));
        return info;
    }
}
