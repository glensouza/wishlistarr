using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public enum WebPushDisposition
{
    Accepted,
    Gone,
    Retry,
}

public readonly record struct WebPushResult(WebPushDisposition Disposition, string? Error, TimeSpan? RetryAfter);

public sealed class WebPushSender(IHttpClientFactory clients, VapidKeys keys)
{
    public const string ClientName = "webpush";
    public const int TimeToLiveSeconds = 86400;

    // RFC 8291 section 4: a 4096-octet body fits at most 3993 octets of plaintext after the 86-octet header, one delimiter and the 16-octet tag.
    private const int MaxPlaintext = 3993;

    public async Task<WebPushResult> SendAsync(WebPushSubscription subscription, string title, string body, CancellationToken ct)
    {
        byte[] plaintext = Fit(title, body);
        byte[] userAgent = Base64Url.DecodeFromChars(subscription.P256dh);
        byte[] auth = Base64Url.DecodeFromChars(subscription.Auth);
        byte[] encrypted = WebPushEncryption.Seal(plaintext, userAgent, auth);
        string authorization = await keys.CreateAuthorizationAsync(new Uri(subscription.Endpoint), ct);
        using HttpRequestMessage request = new(HttpMethod.Post, subscription.Endpoint);
        request.Headers.TryAddWithoutValidation("TTL", TimeToLiveSeconds.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", "normal");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Content = new ByteArrayContent(encrypted);
        request.Content.Headers.ContentType = null;
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using HttpClient client = clients.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return Interpret(response);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return new WebPushResult(WebPushDisposition.Retry, "The push service could not be reached.", null);
        }
    }

    public static WebPushResult Interpret(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        if (status == (int)HttpStatusCode.Created)
        {
            return new WebPushResult(WebPushDisposition.Accepted, null, null);
        }

        if (status is (int)HttpStatusCode.NotFound or (int)HttpStatusCode.Gone)
        {
            return new WebPushResult(WebPushDisposition.Gone, null, null);
        }

        string error = status switch
        {
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => $"The push service rejected the application signature (HTTP {status}).",
            (int)HttpStatusCode.TooManyRequests => "The push service is limiting how often messages can be sent (HTTP 429).",
            >= 300 and < 400 => $"The push service redirected (HTTP {status}).",
            _ => $"Web push delivery returned HTTP {status}.",
        };
        TimeSpan? retry = status == (int)HttpStatusCode.TooManyRequests ? response.Headers.RetryAfter?.Delta : null;
        return new WebPushResult(WebPushDisposition.Retry, error, retry);
    }

    private static byte[] Fit(string title, string body)
    {
        byte[] encoded = Encode(title, body);
        while (encoded.Length > MaxPlaintext && body.Length > 0)
        {
            body = body[..^1];
            encoded = Encode(title, body);
        }

        return encoded;
    }

    private static byte[] Encode(string title, string body) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["title"] = title,
            ["body"] = body,
        });
}
