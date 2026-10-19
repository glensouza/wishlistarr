using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Plex;

internal sealed class PlexServerClient(IHttpClientFactory factory, IOptions<PlexOptions> options)
{
    public const string HttpClientName = "Wishlistarr.Plex.Server";

    private const string Service = "The Plex server";

    public async Task<string?> GetMachineIdentifierAsync(Uri baseUri, string clientIdentifier, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.ProbeTimeout);
        try
        {
            using HttpRequestMessage request = PlexHttp.Create(
                HttpMethod.Get,
                new Uri(baseUri, "identity"),
                options.Value.Product,
                clientIdentifier,
                null);
            using HttpResponseMessage response = await factory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            PlexEnvelope? envelope = await response.Content.ReadFromJsonAsync<PlexEnvelope>(PlexHttp.Json, timeout.Token);
            return envelope?.MediaContainer?.MachineIdentifier;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public Task<PlexContainer> GetRootAsync(PlexServerContext server, CancellationToken ct) =>
        this.GetAsync(server, string.Empty, ct);

    public Task<PlexContainer> GetSectionsAsync(PlexServerContext server, CancellationToken ct) =>
        this.GetAsync(server, "library/sections", ct);

    public Task<PlexContainer> GetSectionItemsAsync(PlexServerContext server, string sectionKey, int type, int start, int size, CancellationToken ct) =>
        this.GetAsync(
            server,
            $"library/sections/{Uri.EscapeDataString(sectionKey)}/all?type={Number(type)}&includeGuids=1"
                + $"&X-Plex-Container-Start={Number(start)}&X-Plex-Container-Size={Number(size)}",
            ct);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<PlexContainer> GetAsync(PlexServerContext server, string path, CancellationToken ct)
    {
        using HttpRequestMessage request = PlexHttp.Create(
            HttpMethod.Get,
            new Uri(server.BaseUri, path),
            options.Value.Product,
            server.ClientIdentifier,
            server.Token);
        using HttpResponseMessage response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);
        PlexEnvelope envelope = await PlexHttp.ReadAsync<PlexEnvelope>(response, Service, ct);
        return envelope.MediaContainer ?? throw new MediaServerException($"{Service} returned an empty response.");
    }
}
