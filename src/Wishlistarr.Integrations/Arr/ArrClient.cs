using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wishlistarr.Core.Fulfilment;

namespace Wishlistarr.Integrations.Arr;

public sealed class ArrClient(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "Wishlistarr.Arr";

    public async Task<JsonNode?> SendAsync(
        ArrService service,
        ArrConnection connection,
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, new Uri(connection.Url, path));
        request.Headers.Add("X-Api-Key", connection.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        string name = service.ToString();
        try
        {
            using HttpClient client = httpClients.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ArrException(Describe(name, response.StatusCode, text));
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (HttpRequestException)
        {
            throw new ArrException($"{name} could not be reached. Check the address and that {name} is running.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ArrException($"{name} did not answer in time.");
        }
        catch (JsonException)
        {
            throw new ArrException($"{name} returned something unexpected. Check that the address points at {name}.");
        }
    }

    private static string Describe(string name, HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{name} rejected the API key.",
        HttpStatusCode.NotFound => $"{name} answered 404. Check the address, including any URL base.",
        HttpStatusCode.BadRequest when ValidationMessage(body) is { } message => $"{name} refused the request: {message}",
        _ => $"{name} answered {(int)status}.",
    };

    private static string? ValidationMessage(string body)
    {
        try
        {
            string? message = JsonNode.Parse(body) switch
            {
                JsonArray { Count: > 0 } array => array[0]?["errorMessage"]?.GetValue<string>(),
                JsonObject obj => obj["message"]?.GetValue<string>(),
                _ => null,
            };
            return message is { Length: > 0 } ? message[..Math.Min(message.Length, 200)] : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
