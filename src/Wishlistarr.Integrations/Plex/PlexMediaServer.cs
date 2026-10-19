using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Plex;

internal sealed partial class PlexMediaServer(
    PlexTvClient plexTv,
    PlexServerClient servers,
    IAppSettings settings,
    IOptions<PlexOptions> options,
    TimeProvider time) : IMediaServer, IMediaServerAccount, IDisposable
{
    private static readonly TimeSpan DefaultPinLifetime = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim identifierLock = new(1, 1);
    private readonly MemoryCache pendingLinks = new(new MemoryCacheOptions { SizeLimit = 100 });
    private readonly MemoryCache lookups = new(new MemoryCacheOptions { SizeLimit = 16 });

    public async Task<MediaServerAccountStatus> GetStatusAsync(CancellationToken ct = default)
    {
        string clientIdentifier = await this.GetClientIdentifierAsync(ct);
        bool linked = await settings.GetAsync(PlexSettingKeys.Token, ct) is { Length: > 0 };
        return new MediaServerAccountStatus(
            linked,
            clientIdentifier,
            linked ? await settings.GetAsync(PlexSettingKeys.AccountName, ct) : null,
            linked ? await this.GetSelectedServerAsync(ct) : null);
    }

    public async Task<MediaServerLinkStart> StartLinkAsync(CancellationToken ct = default)
    {
        string clientIdentifier = await this.GetClientIdentifierAsync(ct);
        PlexPin pin = await plexTv.CreatePinAsync(clientIdentifier, ct);
        if (pin.Id <= 0 || string.IsNullOrWhiteSpace(pin.Code))
        {
            throw new MediaServerException("Plex returned an invalid link code.");
        }

        DateTimeOffset expiresAt = pin.ExpiresAt
            ?? time.GetUtcNow().Add(pin.ExpiresIn is > 0 ? TimeSpan.FromSeconds(pin.ExpiresIn.Value) : DefaultPinLifetime);
        string id = pin.Id.ToString(CultureInfo.InvariantCulture);
        this.pendingLinks.Set(id, expiresAt, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = expiresAt });
        return new MediaServerLinkStart(id, pin.Code, this.BuildAuthUrl(clientIdentifier, pin.Code), expiresAt);
    }

    public async Task<MediaServerLinkCheck?> CheckLinkAsync(string linkId, CancellationToken ct = default)
    {
        if (!long.TryParse(linkId, NumberStyles.None, CultureInfo.InvariantCulture, out long pinId)
            || !this.pendingLinks.TryGetValue(linkId, out DateTimeOffset expiresAt))
        {
            return null;
        }

        string clientIdentifier = await this.GetClientIdentifierAsync(ct);
        PlexPin? pin = await plexTv.GetPinAsync(pinId, clientIdentifier, ct);
        if (pin is null || (string.IsNullOrEmpty(pin.AuthToken) && time.GetUtcNow() >= (pin.ExpiresAt ?? expiresAt)))
        {
            this.pendingLinks.Remove(linkId);
            return new MediaServerLinkCheck(MediaServerLinkState.Expired);
        }

        if (string.IsNullOrEmpty(pin.AuthToken))
        {
            return new MediaServerLinkCheck(MediaServerLinkState.Pending);
        }

        PlexAccount account = await plexTv.GetAccountAsync(clientIdentifier, pin.AuthToken, ct);
        string accountId = account.Id.ToString(CultureInfo.InvariantCulture);
        string accountName = FirstNonEmpty(account.Title, account.Username) ?? "Plex account";
        if (await settings.GetAsync(PlexSettingKeys.AccountId, ct) != accountId)
        {
            await this.ClearAsync(PlexSettingKeys.Server, ct);
        }

        await settings.SetAsync(PlexSettingKeys.Token, pin.AuthToken, true, ct);
        await settings.SetAsync(PlexSettingKeys.AccountId, accountId, false, ct);
        await settings.SetAsync(PlexSettingKeys.AccountName, accountName, false, ct);
        this.pendingLinks.Remove(linkId);
        return new MediaServerLinkCheck(MediaServerLinkState.Linked, accountName);
    }

    public async Task UnlinkAsync(CancellationToken ct = default)
    {
        await this.ClearAsync([.. PlexSettingKeys.Account, .. PlexSettingKeys.Server], ct);
        this.lookups.Clear();
    }

    public async Task<IReadOnlyList<DiscoveredMediaServer>> GetServersAsync(CancellationToken ct = default)
    {
        (string clientIdentifier, string token) = await this.RequireAccountAsync(ct);
        IReadOnlyList<PlexResource> resources = await plexTv.GetResourcesAsync(clientIdentifier, token, ct);
        return
        [
            .. resources
                .Where(resource => resource.IsServer)
                .Select(resource => new DiscoveredMediaServer(
                    resource.ClientIdentifier!,
                    FirstNonEmpty(resource.Name) ?? resource.ClientIdentifier!,
                    resource.Owned,
                    resource.ProductVersion,
                    PlexConnections.Describe(resource))),
        ];
    }

    public async Task<SelectedMediaServer?> SelectServerAsync(string serverId, CancellationToken ct = default)
    {
        (string clientIdentifier, string token) = await this.RequireAccountAsync(ct);
        IReadOnlyList<PlexResource> resources = await plexTv.GetResourcesAsync(clientIdentifier, token, ct);
        PlexResource? server = resources.FirstOrDefault(resource => resource.IsServer && resource.ClientIdentifier == serverId);
        if (server is null)
        {
            return null;
        }

        foreach (MediaServerConnection connection in PlexConnections.Order(server))
        {
            if (await servers.GetMachineIdentifierAsync(connection.Uri, clientIdentifier, ct) == server.ClientIdentifier)
            {
                SelectedMediaServer selected = new(server.ClientIdentifier!, FirstNonEmpty(server.Name) ?? server.ClientIdentifier!, connection);
                await this.SaveServerAsync(selected, FirstNonEmpty(server.AccessToken) ?? token, ct);
                return selected;
            }
        }

        throw new MediaServerException("None of the Plex server's connections answered.");
    }

    public async Task<MediaServerTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (await settings.GetAsync(PlexSettingKeys.Token, ct) is not { Length: > 0 })
        {
            return new MediaServerTestResult(false, "Plex is not linked.");
        }

        if (await settings.GetAsync(PlexSettingKeys.ServerId, ct) is not { Length: > 0 })
        {
            return new MediaServerTestResult(false, "No Plex server is selected.");
        }

        try
        {
            PlexContainer root = await this.WithServerAsync(servers.GetRootAsync, ct);
            string name = FirstNonEmpty(root.FriendlyName) ?? "the Plex server";
            return new MediaServerTestResult(true, $"Connected to {name}.", name, root.Version);
        }
        catch (MediaServerException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new MediaServerTestResult(false, "The Plex server rejected the saved sign-in.");
        }
    }

    public async Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        PlexContainer container = await this.WithServerAsync(servers.GetSectionsAsync, ct);
        return
        [
            .. (container.Directory ?? [])
                .Where(section => IsValidKey(section.Key))
                .Select(section => new MediaLibrary(section.Key!, FirstNonEmpty(section.Title) ?? section.Key!, ToLibraryType(section.Type), section.Agent)),
        ];
    }

    public async Task<IReadOnlyList<MediaServerUser>> GetUsersAsync(CancellationToken ct = default)
    {
        (string clientIdentifier, string token) = await this.RequireAccountAsync(ct);
        IReadOnlyList<PlexHomeUser> home = await plexTv.GetHomeUsersAsync(clientIdentifier, token, ct);
        IReadOnlyList<PlexFriend> friends = await plexTv.GetFriendsAsync(clientIdentifier, token, ct);

        List<MediaServerUser> users = [];
        HashSet<long> seen = [];
        foreach (PlexHomeUser user in home.Where(user => seen.Add(user.Id)))
        {
            users.Add(new MediaServerUser(Id(user.Id), Name(user.Title, user.Username, user.Id), Blank(user.Username), true, user.Restricted, user.Admin));
        }

        foreach (PlexFriend friend in friends.Where(friend => friend.Id > 0 && seen.Add(friend.Id)))
        {
            users.Add(new MediaServerUser(Id(friend.Id), Name(friend.Title, friend.Username, friend.Id), Blank(friend.Username), friend.Home, friend.Restricted, false));
        }

        return users;

        static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

        static string Name(string? title, string? username, long id) => FirstNonEmpty(title, username) ?? Id(id);

        static string? Blank(string? value) => FirstNonEmpty(value);
    }

    public async Task<IReadOnlyList<MediaItem>> FindByExternalIdAsync(MediaKind kind, ExternalId id, CancellationToken ct = default)
    {
        IReadOnlyDictionary<ExternalId, List<MediaItem>> index = await this.WithServerAsync(
            (server, token) => this.GetLookupAsync(server, kind, token),
            ct);
        return index.TryGetValue(id, out List<MediaItem>? items) ? items : [];
    }

    public void Dispose()
    {
        this.identifierLock.Dispose();
        this.pendingLinks.Dispose();
        this.lookups.Dispose();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool IsValidKey(string? key) => key is not null && SectionKey().IsMatch(key);

    private static MediaLibraryType ToLibraryType(string? type) => type switch
    {
        "movie" => MediaLibraryType.Movie,
        "show" => MediaLibraryType.Show,
        "artist" => MediaLibraryType.Music,
        "photo" => MediaLibraryType.Photo,
        _ => MediaLibraryType.Other,
    };

    private async Task<IReadOnlyDictionary<ExternalId, List<MediaItem>>> GetLookupAsync(PlexServerContext server, MediaKind kind, CancellationToken ct)
    {
        string cacheKey = $"{server.ServerId}:{kind}";
        if (this.lookups.TryGetValue(cacheKey, out IReadOnlyDictionary<ExternalId, List<MediaItem>>? cached) && cached is not null)
        {
            return cached;
        }

        (string sectionType, int itemType) = kind == MediaKind.Movie ? ("movie", 1) : ("show", 2);
        int pageSize = Math.Max(1, options.Value.PageSize);
        PlexContainer sections = await servers.GetSectionsAsync(server, ct);
        Dictionary<ExternalId, List<MediaItem>> index = [];
        foreach (PlexSection section in (sections.Directory ?? []).Where(section => section.Type == sectionType && IsValidKey(section.Key)))
        {
            for (int start = 0; ; start += pageSize)
            {
                PlexContainer page = await servers.GetSectionItemsAsync(server, section.Key!, itemType, start, pageSize, ct);
                IReadOnlyList<PlexMetadata> items = page.Metadata ?? [];
                foreach (PlexMetadata item in items.Where(item => !string.IsNullOrWhiteSpace(item.RatingKey)))
                {
                    IReadOnlyList<ExternalId> ids = PlexGuids.Collect(item);
                    MediaItem media = new(item.RatingKey!, item.Guid ?? string.Empty, item.Title ?? string.Empty, item.Year, kind, section.Key!, ids);
                    foreach (ExternalId id in ids)
                    {
                        if (!index.TryGetValue(id, out List<MediaItem>? matches))
                        {
                            index[id] = matches = [];
                        }

                        matches.Add(media);
                    }
                }

                if (items.Count < pageSize || (page.TotalSize is int total && start + items.Count >= total))
                {
                    break;
                }
            }
        }

        this.lookups.Set(cacheKey, (IReadOnlyDictionary<ExternalId, List<MediaItem>>)index, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = options.Value.LookupCacheLifetime,
        });
        return index;
    }

    private async Task<T> WithServerAsync<T>(Func<PlexServerContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        PlexServerContext server = await this.RequireServerAsync(ct);
        try
        {
            return await action(server, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            if (await this.SelectServerAsync(server.ServerId, ct) is null)
            {
                throw new MediaServerException("The selected Plex server is no longer available to this account.");
            }

            return await action(await this.RequireServerAsync(ct), ct);
        }
    }

    private async Task<PlexServerContext> RequireServerAsync(CancellationToken ct)
    {
        (string clientIdentifier, _) = await this.RequireAccountAsync(ct);
        string? serverId = await settings.GetAsync(PlexSettingKeys.ServerId, ct);
        string? url = await settings.GetAsync(PlexSettingKeys.ServerUrl, ct);
        string? token = await settings.GetAsync(PlexSettingKeys.ServerToken, ct);
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(token)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !PlexConnections.IsAllowed(uri))
        {
            throw new MediaServerNotConfiguredException("No Plex server is selected.");
        }

        return new PlexServerContext(serverId, uri, token, clientIdentifier);
    }

    private async Task<(string ClientIdentifier, string Token)> RequireAccountAsync(CancellationToken ct)
    {
        string? token = await settings.GetAsync(PlexSettingKeys.Token, ct);
        if (string.IsNullOrEmpty(token))
        {
            throw new MediaServerNotConfiguredException("Plex is not linked.");
        }

        return (await this.GetClientIdentifierAsync(ct), token);
    }

    private async Task<SelectedMediaServer?> GetSelectedServerAsync(CancellationToken ct)
    {
        string? id = await settings.GetAsync(PlexSettingKeys.ServerId, ct);
        string? url = await settings.GetAsync(PlexSettingKeys.ServerUrl, ct);
        if (string.IsNullOrEmpty(id) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !PlexConnections.IsAllowed(uri))
        {
            return null;
        }

        return new SelectedMediaServer(
            id,
            await settings.GetAsync(PlexSettingKeys.ServerName, ct) ?? id,
            new MediaServerConnection(
                uri,
                await settings.GetAsync(PlexSettingKeys.ServerLocal, ct) == "true",
                await settings.GetAsync(PlexSettingKeys.ServerRelay, ct) == "true",
                uri.Scheme == Uri.UriSchemeHttps));
    }

    private async Task SaveServerAsync(SelectedMediaServer server, string token, CancellationToken ct)
    {
        await settings.SetAsync(PlexSettingKeys.ServerToken, token, true, ct);
        await settings.SetAsync(PlexSettingKeys.ServerName, server.Name, false, ct);
        await settings.SetAsync(PlexSettingKeys.ServerUrl, server.Connection.Uri.ToString(), false, ct);
        await settings.SetAsync(PlexSettingKeys.ServerLocal, server.Connection.Local ? "true" : "false", false, ct);
        await settings.SetAsync(PlexSettingKeys.ServerRelay, server.Connection.Relay ? "true" : "false", false, ct);
        await settings.SetAsync(PlexSettingKeys.ServerId, server.Id, false, ct);
    }

    private async Task ClearAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (string key in keys)
        {
            await settings.DeleteAsync(key, ct);
        }
    }

    private async Task<string> GetClientIdentifierAsync(CancellationToken ct)
    {
        if (await settings.GetAsync(PlexSettingKeys.ClientIdentifier, ct) is { Length: > 0 } existing)
        {
            return existing;
        }

        await this.identifierLock.WaitAsync(ct);
        try
        {
            if (await settings.GetAsync(PlexSettingKeys.ClientIdentifier, ct) is { Length: > 0 } stored)
            {
                return stored;
            }

            string created = Guid.NewGuid().ToString("D");
            await settings.SetAsync(PlexSettingKeys.ClientIdentifier, created, false, ct);
            return created;
        }
        finally
        {
            this.identifierLock.Release();
        }
    }

    private Uri BuildAuthUrl(string clientIdentifier, string code)
    {
        string fragment = string.Join(
            '&',
            $"clientID={Uri.EscapeDataString(clientIdentifier)}",
            $"code={Uri.EscapeDataString(code)}",
            $"{Uri.EscapeDataString("context[device][product]")}={Uri.EscapeDataString(options.Value.Product)}");
        return new Uri($"{options.Value.AuthAppAddress.GetLeftPart(UriPartial.Path)}#?{fragment}");
    }

    [GeneratedRegex(@"^[A-Za-z0-9]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SectionKey();
}
