using System.Net;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Plex;

internal sealed class PlexRestrictionReader(IHttpClientFactory factory, IAppSettings settings, IOptions<PlexOptions> options)
    : IMediaServerRestrictions
{
    public async Task<IReadOnlyDictionary<string, UserContentFilters>?> GetUserFiltersAsync(CancellationToken ct = default)
    {
        string? token = await settings.GetAsync(PlexSettingKeys.Token, ct);
        string? clientIdentifier = await settings.GetAsync(PlexSettingKeys.ClientIdentifier, ct);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(clientIdentifier))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = PlexHttp.Create(
                HttpMethod.Get,
                new Uri("api/users", UriKind.Relative),
                options.Value.Product,
                clientIdentifier,
                token);
            request.Headers.Accept.Clear();
            using HttpResponseMessage response = await factory.CreateClient(PlexTvClient.HttpClientName).SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
            return Parse(document);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Dictionary<string, UserContentFilters>? Parse(XDocument document)
    {
        if (document.Root is not { Name.LocalName: "MediaContainer" } root)
        {
            return null;
        }

        Dictionary<string, UserContentFilters> users = new(StringComparer.Ordinal);
        foreach (XElement user in root.Elements("User"))
        {
            if (((string?)user.Attribute("id")) is { Length: > 0 } id)
            {
                users[id] = new UserContentFilters(
                    (string?)user.Attribute("filterAll"),
                    (string?)user.Attribute("filterMovies"),
                    (string?)user.Attribute("filterTelevision"));
            }
        }

        return users;
    }
}
