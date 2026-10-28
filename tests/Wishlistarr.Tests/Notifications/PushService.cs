using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wishlistarr.Api.Notifications;

namespace Wishlistarr.Tests.Notifications;

public sealed record PushDelivery(
    string Path,
    string? Ttl,
    string? ContentEncoding,
    string? ContentType,
    string Authorization,
    byte[] Body,
    string? Plaintext);

public sealed class PushSubscriber
{
    internal PushSubscriber(ECDiffieHellman key, byte[] auth, Uri endpoint)
    {
        this.Key = key;
        this.Auth = Base64Url.EncodeToString(auth);
        this.Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public string PublicKey => Base64Url.EncodeToString(WebPushEncryption.Uncompressed(this.Key.ExportParameters(false).Q));

    public string Auth { get; }

    internal ECDiffieHellman Key { get; }

    internal byte[] AuthSecret => Base64Url.DecodeFromChars(this.Auth);
}

/// <summary>
/// Local stand-in for a push service and the browser that owns the subscription.
/// HMAC is implemented here with <see cref="HMACSHA256"/> so a passing decrypt does not depend on <c>HKDF.DeriveKey</c>.
/// </summary>
public sealed class PushService : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly Dictionary<string, PushSubscriber> subscribers = [];
    private readonly List<PushDelivery> messages = [];
    private int next = 1;

    private PushService(WebApplication app, Uri address)
    {
        this.app = app;
        this.Address = address;
    }

    public Uri Address { get; }

    public string? ApplicationServerKey { get; set; }

    public int? ForceStatus { get; set; }

    public int? RetryAfterSeconds { get; set; }

    public IReadOnlyList<PushDelivery> Messages
    {
        get
        {
            lock (this.messages)
            {
                return [.. this.messages];
            }
        }
    }

    public static async Task<PushService> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        PushService? service = null;
        app.MapPost("/{**path}", async context => await service!.ReceiveAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        service = new PushService(app, new Uri(address));
        return service;
    }

    public PushSubscriber Add()
    {
        ECDiffieHellman key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PushSubscriber subscriber = new(key, RandomNumberGenerator.GetBytes(16), new Uri(this.Address, $"push/{this.next}"));
        this.next++;
        this.subscribers[subscriber.Endpoint.AbsolutePath] = subscriber;
        return subscriber;
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.DisposeAsync();
        foreach (PushSubscriber subscriber in this.subscribers.Values)
        {
            subscriber.Key.Dispose();
        }
    }

    private async Task ReceiveAsync(HttpContext context)
    {
        string? ttl = context.Request.Headers.TryGetValue("TTL", out Microsoft.Extensions.Primitives.StringValues ttlValues)
            ? ttlValues.ToString()
            : null;
        if (ttl is null || !int.TryParse(ttl, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string authorization = context.Request.Headers.TryGetValue("Authorization", out Microsoft.Extensions.Primitives.StringValues authorizationValues)
            ? authorizationValues.ToString()
            : string.Empty;
        if (!this.ValidVapid(authorization, $"{context.Request.Scheme}://{context.Request.Host}"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!this.subscribers.TryGetValue(context.Request.Path.Value ?? string.Empty, out PushSubscriber? subscriber))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using MemoryStream buffer = new();
        await context.Request.Body.CopyToAsync(buffer);
        byte[] body = buffer.ToArray();
        string? plaintext = null;
        if (body.Length > 0)
        {
            try
            {
                plaintext = Open(body, subscriber);
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException or InvalidOperationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }

        lock (this.messages)
        {
            this.messages.Add(new PushDelivery(
                context.Request.Path.Value ?? string.Empty,
                ttl,
                context.Request.Headers.ContentEncoding.ToString(),
                context.Request.ContentType,
                authorization,
                body,
                plaintext));
        }

        int status = this.ForceStatus ?? StatusCodes.Status201Created;
        if (status == StatusCodes.Status429TooManyRequests && this.RetryAfterSeconds is int retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        context.Response.StatusCode = status;
    }

    private bool ValidVapid(string authorization, string audience)
    {
        if (!authorization.StartsWith("vapid t=", StringComparison.Ordinal))
        {
            return false;
        }

        int keyIndex = authorization.IndexOf(",k=", StringComparison.Ordinal);
        if (keyIndex <= "vapid t=".Length)
        {
            return false;
        }

        string token = authorization["vapid t=".Length..keyIndex];
        string key = authorization[(keyIndex + 3)..];
        if (this.ApplicationServerKey is not null && !string.Equals(key, this.ApplicationServerKey, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            using JsonDocument header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256")
            {
                return false;
            }

            byte[] signature = Base64Url.DecodeFromChars(parts[2]);
            if (signature.Length != 64 || !WebPushEncryption.TryPublic(Base64Url.DecodeFromChars(key), out ECParameters parameters))
            {
                return false;
            }

            using ECDsa ecdsa = ECDsa.Create(parameters);
            byte[] signed = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{parts[0]}.{parts[1]}"));
            if (!ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return false;
            }

            using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            if (claims.RootElement.GetProperty("aud").GetString() != audience
                || !claims.RootElement.GetProperty("exp").TryGetInt64(out long exp))
            {
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return exp >= now && exp <= now + 86400;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or KeyNotFoundException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Open(byte[] body, PushSubscriber subscriber)
    {
        if (body.Length < 21 + WebPushEncryption.PublicKeyLength + 16)
        {
            throw new InvalidOperationException("The push body is shorter than an aes128gcm header.");
        }

        byte[] salt = body[..16];
        int idLength = body[20];
        if (idLength != WebPushEncryption.PublicKeyLength || body.Length < 21 + idLength + 16)
        {
            throw new InvalidOperationException("The push body is missing the ephemeral key.");
        }

        byte[] serverPublic = body[21..(21 + idLength)];
        byte[] encrypted = body[(21 + idLength)..];
        if (!WebPushEncryption.TryPublic(serverPublic, out ECParameters server))
        {
            throw new CryptographicException("The ephemeral key is not a P-256 point.");
        }

        byte[] userAgent = Base64Url.DecodeFromChars(subscriber.PublicKey);
        using ECDiffieHellman peer = ECDiffieHellman.Create(server);
        byte[] secret = Pad32(subscriber.Key.DeriveRawSecretAgreement(peer.PublicKey));
        byte[] ikm = Expand(secret, 32, subscriber.AuthSecret, Info("WebPush: info"u8, userAgent, serverPublic));
        byte[] contentKey = Expand(ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8);
        byte[] nonce = Expand(ikm, 12, salt, "Content-Encoding: nonce\0"u8);
        byte[] cipher = encrypted[..^16];
        byte[] tag = encrypted[^16..];
        byte[] plain = new byte[cipher.Length];
        using AesGcm aes = new(contentKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        int end = plain.Length;
        while (end > 0 && plain[end - 1] == 0)
        {
            end--;
        }

        if (end == 0 || plain[end - 1] != 2)
        {
            throw new InvalidOperationException("The push record delimiter is missing.");
        }

        return Encoding.UTF8.GetString(plain.AsSpan(0, end - 1));
    }

    private static byte[] Info(ReadOnlySpan<byte> label, ReadOnlySpan<byte> userAgent, ReadOnlySpan<byte> server)
    {
        byte[] info = new byte[label.Length + 1 + userAgent.Length + server.Length];
        label.CopyTo(info);
        userAgent.CopyTo(info.AsSpan(label.Length + 1));
        server.CopyTo(info.AsSpan(label.Length + 1 + userAgent.Length));
        return info;
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

    private static byte[] Expand(ReadOnlySpan<byte> ikm, int length, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        byte[] prk = HMACSHA256.HashData(salt, ikm);
        byte[] okm = new byte[length];
        byte[] previous = [];
        int filled = 0;
        for (byte counter = 1; filled < length; counter++)
        {
            byte[] block = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(block);
            info.CopyTo(block.AsSpan(previous.Length));
            block[^1] = counter;
            previous = HMACSHA256.HashData(prk, block);
            int take = Math.Min(previous.Length, length - filled);
            previous.AsSpan(0, take).CopyTo(okm.AsSpan(filled));
            filled += take;
        }

        CryptographicOperations.ZeroMemory(prk);
        return okm;
    }
}
