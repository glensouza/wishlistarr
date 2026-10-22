using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Plex;

internal sealed class PlexRestrictionReader(IHttpClientFactory factory, IAppSettings settings, IOptions<PlexOptions> options)
    : IMediaServerRestrictions
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 4 });

    public async Task<IReadOnlyDictionary<string, UserContentFilters>?> GetUserFiltersAsync(CancellationToken ct = default)
    {
        try
        {
            string? token = await settings.GetAsync(PlexSettingKeys.Token, ct);
            string? clientIdentifier = await settings.GetAsync(PlexSettingKeys.ClientIdentifier, ct);
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(clientIdentifier))
            {
                return null;
            }

            string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{token}:{clientIdentifier}")));
            if (this.cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, UserContentFilters>? cached))
            {
                return cached;
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.RestrictionTimeout);
            using HttpRequestMessage request = PlexHttp.Create(
                HttpMethod.Get,
                new Uri("api/users", UriKind.Relative),
                options.Value.Product,
                clientIdentifier,
                token);
            request.Headers.Accept.Clear();
            using HttpResponseMessage response = await factory.CreateClient(PlexTvClient.HttpClientName).SendAsync(request, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, timeout.Token);
            Dictionary<string, UserContentFilters>? users = Parse(document);
            if (users is not null && options.Value.RestrictionCacheLifetime > TimeSpan.Zero)
            {
                this.cache.Set(cacheKey, (IReadOnlyDictionary<string, UserContentFilters>)users, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = options.Value.RestrictionCacheLifetime,
                });
            }

            return users;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
