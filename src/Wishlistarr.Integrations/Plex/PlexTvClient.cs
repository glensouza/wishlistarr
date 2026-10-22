using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Plex;

internal sealed class PlexTvClient(IHttpClientFactory factory, IOptions<PlexOptions> options)
{
    public const string HttpClientName = "Wishlistarr.Plex.Tv";

    private const string Service = "Plex";

    public async Task<PlexPin> CreatePinAsync(string clientIdentifier, CancellationToken ct)
    {
        using HttpRequestMessage request = this.Create(HttpMethod.Post, "api/v2/pins?strong=true", clientIdentifier, null);
        using HttpResponseMessage response = await this.SendAsync(request, ct);
        return await PlexHttp.ReadAsync<PlexPin>(response, Service, ct);
    }

    public async Task<PlexPin?> GetPinAsync(long id, string clientIdentifier, CancellationToken ct)
    {
        using HttpRequestMessage request = this.Create(
            HttpMethod.Get,
            $"api/v2/pins/{id.ToString(CultureInfo.InvariantCulture)}",
            clientIdentifier,
            null);
        using HttpResponseMessage response = await this.SendAsync(request, ct);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await PlexHttp.ReadAsync<PlexPin>(response, Service, ct);
    }

    public async Task<PlexAccount> GetAccountAsync(string clientIdentifier, string token, CancellationToken ct)
    {
        using HttpRequestMessage request = this.Create(HttpMethod.Get, "api/v2/user", clientIdentifier, token);
        using HttpResponseMessage response = await this.SendAsync(request, ct);
        return await PlexHttp.ReadAsync<PlexAccount>(response, Service, ct);
    }

    public async Task<IReadOnlyList<PlexResource>> GetResourcesAsync(string clientIdentifier, string token, CancellationToken ct)
    {
        using HttpRequestMessage request = this.Create(
            HttpMethod.Get,
            "api/v2/resources?includeHttps=1&includeRelay=1&includeIPv6=1",
            clientIdentifier,
            token);
        using HttpResponseMessage response = await this.SendAsync(request, ct);
        return await PlexHttp.ReadAsync<List<PlexResource>>(response, Service, ct);
    }

    public async Task<IReadOnlyList<PlexFriend>> GetFriendsAsync(string clientIdentifier, string token, CancellationToken ct)
    {
        XDocument? document = await this.GetXmlAsync("api/users", clientIdentifier, token, ct);
        return
        [
            .. (document?.Root?.Elements("User") ?? [])
                .Select(user => (User: user, Id: ParseLong((string?)user.Attribute("id"))))
                .Where(entry => entry.Id > 0)
                .Select(entry => new PlexFriend(
                    entry.Id,
                    (string?)entry.User.Attribute("title"),
                    (string?)entry.User.Attribute("username"),
                    IsTrue((string?)entry.User.Attribute("restricted")),
                    IsTrue((string?)entry.User.Attribute("home")))),
        ];
    }

    public async Task<IReadOnlyList<PlexHomeUser>> GetHomeUsersAsync(string clientIdentifier, string token, CancellationToken ct)
    {
        XDocument? document = await this.GetXmlAsync("api/home/users", clientIdentifier, token, ct);
        return
        [
            .. (document?.Root?.Elements("User") ?? [])
                .Select(user => (User: user, Id: ParseLong((string?)user.Attribute("id"))))
                .Where(entry => entry.Id > 0)
                .Select(entry => new PlexHomeUser(
                    entry.Id,
                    (string?)entry.User.Attribute("title"),
                    (string?)entry.User.Attribute("username"),
                    IsTrue((string?)entry.User.Attribute("restricted")),
                    IsTrue((string?)entry.User.Attribute("admin")))),
        ];
    }

    private async Task<XDocument?> GetXmlAsync(string path, string clientIdentifier, string token, CancellationToken ct)
    {
        using HttpRequestMessage request = this.Create(HttpMethod.Get, path, clientIdentifier, token);
        request.Headers.Accept.Clear();
        using HttpResponseMessage response = await this.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        PlexHttp.EnsureSuccess(response, Service);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        return await XDocument.LoadAsync(reader, LoadOptions.None, ct);
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) ? result : 0;

    private static bool IsTrue(string? value) => value is "1" or "true";

    private HttpRequestMessage Create(HttpMethod method, string path, string clientIdentifier, string? token) =>
        PlexHttp.Create(method, new Uri(path, UriKind.Relative), options.Value.Product, clientIdentifier, token);

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        factory.CreateClient(HttpClientName).SendAsync(request, ct);
}
