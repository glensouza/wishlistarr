using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Plex;

internal sealed partial class PlexMediaServer(
    PlexTvClient plexTv,
    PlexServerClient servers,
    IAppSettings settings,
    IOptions<PlexOptions> options,
    TimeProvider time) : IMediaServer, IMediaServerAccount, ILibraryScanner, IDisposable
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

        await settings.SetAsync(
            [
                new AppSettingChange(PlexSettingKeys.Token, pin.AuthToken, true),
                new AppSettingChange(PlexSettingKeys.AccountId, accountId, false),
                new AppSettingChange(PlexSettingKeys.AccountName, accountName, false),
            ],
            ct);
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

        string serverToken = FirstNonEmpty(server.AccessToken)
            ?? (server.Owned ? token : throw new MediaServerException("Plex did not provide an access token for this shared server."));
        foreach (MediaServerConnection connection in PlexConnections.Order(server))
        {
            if (await servers.GetMachineIdentifierAsync(connection.Uri, clientIdentifier, ct) == server.ClientIdentifier)
            {
                SelectedMediaServer selected = new(server.ClientIdentifier!, FirstNonEmpty(server.Name) ?? server.ClientIdentifier!, connection);
                await this.SaveServerAsync(selected, serverToken, ct);
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

    public async Task<MediaItemLabels?> GetLabelsAsync(string itemId, CancellationToken ct = default)
    {
        if (!IsValidKey(itemId))
        {
            return null;
        }

        return await this.WithServerAsync(
            async (server, token) =>
            {
                PlexMetadata? item = await servers.GetMetadataAsync(server, itemId, token);
                string? showKey = item?.Type switch
                {
                    "season" => item.ParentRatingKey,
                    "episode" => item.GrandparentRatingKey,
                    _ => null,
                };
                if (showKey is not null)
                {
                    item = IsValidKey(showKey) ? await servers.GetMetadataAsync(server, showKey, token) : null;
                }

                return item is null ? null : ToLabels(server, item);
            },
            ct);
    }

    public async Task SetLabelsAsync(MediaItemLabels item, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        if (!IsValidKey(item.Id) || !IsValidKey(item.LibraryId))
        {
            throw new MediaServerException("The Plex item cannot be edited.");
        }

        List<string> distinct = [.. labels.Distinct(StringComparer.OrdinalIgnoreCase)];
        List<KeyValuePair<string, string>> parameters =
        [
            new("type", item.Kind == MediaKind.Movie ? "1" : "2"),
            new("id", item.Id),
        ];
        if (distinct.Count > 0)
        {
            parameters.AddRange(distinct.Select((label, index) =>
                new KeyValuePair<string, string>($"label[{index.ToString(CultureInfo.InvariantCulture)}].tag.tag", label)));
        }
        else if (item.Labels.Count > 0)
        {
            parameters.Add(new("label[].tag.tag-", string.Join(',', item.Labels)));
        }

        parameters.Add(new("label.locked", "1"));

        await this.WithServerAsync(
            async (server, token) =>
            {
                if (server.ServerId != item.ServerId)
                {
                    throw new MediaServerException("The selected Plex server has changed.");
                }

                await servers.EditSectionItemAsync(server, item.LibraryId, parameters, token);
                return true;
            },
            ct);
    }

    public async Task<LibraryScan> ScanAsync(DateTimeOffset? updatedSince, CancellationToken ct = default)
    {
        PlexServerContext server = await this.RequireServerAsync(ct);
        LibraryScanReport report = new();
        return new LibraryScan(server.ServerId, this.ReadEntriesAsync(server, updatedSince, report, ct)) { Report = report };
    }

    public async Task<LibraryLabelScan> ScanLabelsAsync(CancellationToken ct = default)
    {
        PlexServerContext server = await this.RequireServerAsync(ct);
        int pageSize = Math.Max(1, options.Value.PageSize);
        Dictionary<string, List<string>> byItem = new(StringComparer.Ordinal);
        bool complete = true;
        PlexContainer sections = await servers.GetSectionsAsync(server, ct);
        foreach (PlexSection section in (sections.Directory ?? []).Where(section => section.Type is "movie" or "show" && IsValidKey(section.Key)))
        {
            int itemType = section.Type == "movie" ? 1 : 2;
            PlexContainer labels = await servers.GetSectionLabelsAsync(server, section.Key!, ct);
            foreach (PlexSection label in (labels.Directory ?? []).Where(label => !string.IsNullOrWhiteSpace(label.Key) && !string.IsNullOrWhiteSpace(label.Title)))
            {
                HashSet<string?> received = [];
                int? announced = null;
                for (int start = 0; ; start += pageSize)
                {
                    PlexContainer page = await servers.GetSectionItemsAsync(server, section.Key!, itemType, start, pageSize, ct, label: label.Key);
                    IReadOnlyList<PlexMetadata> items = page.Metadata ?? [];
                    announced = page.TotalSize ?? announced;
                    foreach (PlexMetadata item in items)
                    {
                        received.Add(item.RatingKey);
                        if (!IsValidKey(item.RatingKey))
                        {
                            continue;
                        }

                        if (!byItem.TryGetValue(item.RatingKey!, out List<string>? tags))
                        {
                            byItem[item.RatingKey!] = tags = [];
                        }

                        tags.Add(label.Title!);
                    }

                    if (items.Count < pageSize || (announced is int total && start + items.Count >= total))
                    {
                        break;
                    }
                }

                if (announced is int expected && received.Count < expected)
                {
                    complete = false;
                }
            }
        }

        return new LibraryLabelScan(
            server.ServerId,
            byItem.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal),
            complete);
    }

    public Task<string?> GetServerIdAsync(CancellationToken ct = default) => settings.GetAsync(PlexSettingKeys.ServerId, ct);

    public async Task<LibrarySnapshot?> GetEntryAsync(string itemId, CancellationToken ct = default)
    {
        if (!IsValidKey(itemId))
        {
            return null;
        }

        return await this.WithServerAsync(
            async (server, token) =>
            {
                PlexMetadata? item = await servers.GetMetadataAsync(server, itemId, token);
                string? showKey = item?.Type switch
                {
                    "season" => item.ParentRatingKey,
                    "episode" => item.GrandparentRatingKey,
                    _ => null,
                };
                if (showKey is not null)
                {
                    item = IsValidKey(showKey) ? await servers.GetMetadataAsync(server, showKey, token) : null;
                }

                MediaKind? kind = item?.Type switch
                {
                    "movie" => MediaKind.Movie,
                    "show" => MediaKind.Show,
                    _ => null,
                };
                string? libraryId = item?.LibrarySectionId?.ToString(CultureInfo.InvariantCulture);
                return item is null || kind is null || !IsValidKey(item.RatingKey) || !IsValidKey(libraryId)
                    ? null
                    : new LibrarySnapshot(server.ServerId, ToEntry(item, kind.Value, libraryId!, true));
            },
            ct);
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

    private static MediaItemLabels ToLabels(PlexServerContext server, PlexMetadata item)
    {
        MediaKind kind = item.Type switch
        {
            "movie" => MediaKind.Movie,
            "show" => MediaKind.Show,
            _ => throw new MediaServerException("Labels can only be set on movies and shows."),
        };
        string? libraryId = item.LibrarySectionId?.ToString(CultureInfo.InvariantCulture);
        if (!IsValidKey(item.RatingKey) || !IsValidKey(libraryId))
        {
            throw new MediaServerException("The Plex server returned an item without a library.");
        }

        return new MediaItemLabels(
            server.ServerId,
            item.RatingKey!,
            item.Guid ?? string.Empty,
            item.Title ?? string.Empty,
            kind,
            libraryId!,
            [.. (item.Labels ?? []).Select(label => label.Tag).OfType<string>().Where(tag => tag.Length > 0)],
            (item.Fields ?? []).Any(field => field.Name == "label" && field.Locked));
    }

    private static LibraryEntry ToEntry(PlexMetadata item, MediaKind kind, string libraryId, bool detailed) => new(
        new MediaItem(item.RatingKey!, item.Guid ?? string.Empty, item.Title ?? string.Empty, item.Year, kind, libraryId, PlexGuids.Collect(item)),
        detailed ? [.. (item.Labels ?? []).Select(label => label.Tag).OfType<string>().Where(tag => tag.Length > 0)] : null,
        item.UpdatedAt is long updated ? DateTimeOffset.FromUnixTimeSeconds(updated) : null);

    private async IAsyncEnumerable<LibraryEntry> ReadEntriesAsync(
        PlexServerContext server,
        DateTimeOffset? updatedSince,
        LibraryScanReport report,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int pageSize = Math.Max(1, options.Value.PageSize);
        long? since = updatedSince?.ToUnixTimeSeconds();
        PlexContainer sections = await servers.GetSectionsAsync(server, ct);
        foreach (PlexSection section in (sections.Directory ?? []).Where(section => section.Type is "movie" or "show" && IsValidKey(section.Key)))
        {
            (MediaKind kind, int itemType) = section.Type == "movie" ? (MediaKind.Movie, 1) : (MediaKind.Show, 2);
            HashSet<string?> received = [];
            int? announced = null;
            for (int start = 0; ; start += pageSize)
            {
                PlexContainer page = await servers.GetSectionItemsAsync(server, section.Key!, itemType, start, pageSize, ct, since);
                IReadOnlyList<PlexMetadata> items = page.Metadata ?? [];
                announced = page.TotalSize ?? announced;
                foreach (PlexMetadata item in items)
                {
                    received.Add(item.RatingKey);
                    if (IsValidKey(item.RatingKey))
                    {
                        yield return ToEntry(item, kind, section.Key!, false);
                    }
                }

                if (items.Count < pageSize || (announced is int total && start + items.Count >= total))
                {
                    break;
                }
            }

            if (announced is int expected && received.Count < expected)
            {
                report.Complete = false;
            }
        }
    }

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
        IReadOnlyDictionary<string, string?> stored = await settings.GetAsync(
            [PlexSettingKeys.ServerId, PlexSettingKeys.ServerUrl, PlexSettingKeys.ServerToken],
            ct);
        string? serverId = stored[PlexSettingKeys.ServerId];
        string? url = stored[PlexSettingKeys.ServerUrl];
        string? token = stored[PlexSettingKeys.ServerToken];
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
        IReadOnlyDictionary<string, string?> stored = await settings.GetAsync(
            [
                PlexSettingKeys.ServerId,
                PlexSettingKeys.ServerName,
                PlexSettingKeys.ServerUrl,
                PlexSettingKeys.ServerLocal,
                PlexSettingKeys.ServerRelay,
            ],
            ct);
        string? id = stored[PlexSettingKeys.ServerId];
        string? url = stored[PlexSettingKeys.ServerUrl];
        if (string.IsNullOrEmpty(id) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !PlexConnections.IsAllowed(uri))
        {
            return null;
        }

        return new SelectedMediaServer(
            id,
            stored[PlexSettingKeys.ServerName] ?? id,
            new MediaServerConnection(
                uri,
                stored[PlexSettingKeys.ServerLocal] == "true",
                stored[PlexSettingKeys.ServerRelay] == "true",
                uri.Scheme == Uri.UriSchemeHttps));
    }

    private async Task SaveServerAsync(SelectedMediaServer server, string token, CancellationToken ct)
    {
        await settings.SetAsync(
            [
                new AppSettingChange(PlexSettingKeys.ServerToken, token, true),
                new AppSettingChange(PlexSettingKeys.ServerName, server.Name, false),
                new AppSettingChange(PlexSettingKeys.ServerUrl, server.Connection.Uri.ToString(), false),
                new AppSettingChange(PlexSettingKeys.ServerLocal, server.Connection.Local ? "true" : "false", false),
                new AppSettingChange(PlexSettingKeys.ServerRelay, server.Connection.Relay ? "true" : "false", false),
                new AppSettingChange(PlexSettingKeys.ServerId, server.Id, false),
            ],
            ct);
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
