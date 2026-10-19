using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Plex;

internal static class PlexHttp
{
    public const string TokenHeader = "X-Plex-Token";
    public const string ClientIdentifierHeader = "X-Plex-Client-Identifier";

    public static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly string Version =
        typeof(PlexHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static HttpRequestMessage Create(HttpMethod method, Uri uri, string product, string clientIdentifier, string? token)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Plex-Product", product);
        request.Headers.Add("X-Plex-Version", Version);
        request.Headers.Add("X-Plex-Device-Name", product);
        request.Headers.Add(ClientIdentifierHeader, clientIdentifier);
        if (token is not null)
        {
            request.Headers.Add(TokenHeader, token);
        }

        return request;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, string service, CancellationToken ct)
        where T : class
    {
        EnsureSuccess(response, service);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new MediaServerException($"{service} returned an empty response.");
    }

    public static void EnsureSuccess(HttpResponseMessage response, string service)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new MediaServerException($"{service} rejected the saved sign-in.", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MediaServerException($"{service} answered {(int)response.StatusCode}.", response.StatusCode);
        }
    }
}
