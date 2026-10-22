using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;

namespace Wishlistarr.Integrations.Seerr;

public sealed class SeerrClient(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "Wishlistarr.Seerr";

    public const string ApiUserHeader = "X-Api-User";

    public async Task<JsonNode?> SendAsync(
        SeerrConnection connection,
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken ct,
        int? actAs = null) =>
        (await this.SendForResponseAsync(connection, method, path, body, ct, actAs)).Body;

    public async Task<SeerrResponse> SendForResponseAsync(
        SeerrConnection connection,
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken ct,
        int? actAs = null)
    {
        Uri target = new(connection.Url, path);
        if (!SameOrigin(connection.Url, target))
        {
            throw new SeerrException("Seerr could not be reached. Check the address and that Seerr is running.");
        }

        if (connection.ApiKey.Any(char.IsControl))
        {
            throw new SeerrException("The saved API key is not valid.");
        }

        using HttpRequestMessage request = new(method, target);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        try
        {
            request.Headers.Add("X-Api-Key", connection.ApiKey);
            if (actAs is int userId)
            {
                request.Headers.Add(ApiUserHeader, userId.ToString(CultureInfo.InvariantCulture));
            }

            using HttpClient client = httpClients.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new SeerrException("Seerr redirected the request. Check the address.", response.StatusCode);
            }

            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new SeerrException(Describe(response.StatusCode, text, connection.ApiKey), response.StatusCode);
            }

            return new SeerrResponse(response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
        }
        catch (SeerrException)
        {
            throw;
        }
        catch (FormatException)
        {
            throw new SeerrException("The saved API key is not valid.");
        }
        catch (HttpRequestException)
        {
            throw new SeerrException("Seerr could not be reached. Check the address and that Seerr is running.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SeerrException("Seerr did not answer in time.");
        }
        catch (JsonException)
        {
            throw new SeerrException("Seerr returned something unexpected. Check that the address points at Seerr.");
        }
    }

    private static bool SameOrigin(Uri saved, Uri target) =>
        string.Equals(saved.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(saved.Host, target.Host, StringComparison.OrdinalIgnoreCase)
        && saved.Port == target.Port;

    private static string Describe(HttpStatusCode status, string body, string apiKey) => Redact(
        status switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict
                when ValidationMessage(body) is { } message => $"Seerr refused the request: {message}",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Seerr rejected the API key.",
            HttpStatusCode.NotFound => "Seerr answered 404. Check the address, including any URL base.",
            _ => $"Seerr answered {(int)status}.",
        },
        apiKey);

    private static string Redact(string message, string apiKey) =>
        apiKey.Length == 0 ? message : message.Replace(apiKey, "****", StringComparison.Ordinal);

    private static string? ValidationMessage(string body)
    {
        try
        {
            string? message = JsonNode.Parse(body) is JsonObject obj ? obj["message"]?.GetValue<string>() : null;
            return message is { Length: > 0 } ? message[..Math.Min(message.Length, 200)] : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed record SeerrResponse(HttpStatusCode Status, JsonNode? Body);
