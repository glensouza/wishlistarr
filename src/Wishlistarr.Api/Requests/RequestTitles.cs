using System.Net;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public sealed class RequestTitles(WishlistarrDbContext db, ITmdbClient tmdb, TmdbRetention retention, TimeProvider time)
{
    public async Task<string?> FetchNameAsync(MediaKind kind, int tmdbId, CancellationToken ct) =>
        (await this.FetchAsync(kind, tmdbId, ct))?.Name;

    public async Task<(string Name, TmdbDetails Details)?> FetchAsync(MediaKind kind, int tmdbId, CancellationToken ct)
    {
        try
        {
            TmdbDetails details = await tmdb.GetDetailsAsync(kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv, tmdbId, ct: ct);
            string name = Clip(details.Title ?? details.Name ?? string.Empty);
            return name.Length == 0 ? null : (name, details);
        }
        catch (TmdbException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string> GetAsync(MediaRequest request, CancellationToken ct) =>
        await this.ResolveAsync(request, await this.StaleBeforeAsync(ct), ct);

    public async Task<DateTimeOffset> StaleBeforeAsync(CancellationToken ct) => time.GetUtcNow() - await retention.GetAsync(ct);

    public static string? Fresh(MediaRequest request, DateTimeOffset staleBefore) =>
        request.Title is { Length: > 0 } stored && request.TitleFetchedAt is DateTimeOffset fetchedAt && fetchedAt >= staleBefore
            ? stored
            : null;

    public async Task<string> StoreAsync(int requestId, string fetched, CancellationToken ct)
    {
        string name = Clip(fetched);
        if (name.Length == 0)
        {
            return string.Empty;
        }

        DateTimeOffset now = time.GetUtcNow();
        await db.MediaRequests.Where(row => row.Id == requestId).ExecuteUpdateAsync(
            setters => setters.SetProperty(row => row.Title, name).SetProperty(row => row.TitleFetchedAt, now),
            ct);
        return name;
    }

    public async Task<IReadOnlyDictionary<int, string>> GetManyAsync(IReadOnlyCollection<MediaRequest> requests, CancellationToken ct)
    {
        Dictionary<int, string> names = [];
        if (requests.Count == 0)
        {
            return names;
        }

        DateTimeOffset staleBefore = await this.StaleBeforeAsync(ct);
        foreach (MediaRequest request in requests)
        {
            names[request.Id] = await this.ResolveAsync(request, staleBefore, ct);
        }

        return names;
    }

    private static string Clip(string name)
    {
        string trimmed = name.Trim();
        return trimmed[..Math.Min(trimmed.Length, MediaRequests.MaxTitleLength)];
    }

    private async Task<string> ResolveAsync(MediaRequest request, DateTimeOffset staleBefore, CancellationToken ct)
    {
        if (Fresh(request, staleBefore) is string stored)
        {
            return stored;
        }

        try
        {
            return await this.FetchNameAsync(request.Kind, request.TmdbId, ct) is string name
                ? await this.StoreAsync(request.Id, name, ct)
                : string.Empty;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
        }
    }
}

public sealed class RequestTmdbDataStore(WishlistarrDbContext db, RequestTitles titles, TimeProvider time) : ITmdbDataStore
{
    private const int ChunkSize = 200;

    private static readonly RequestState[] OpenStates = [RequestState.Pending, RequestState.Approved, RequestState.Fulfilling];

    public Type EntityType => typeof(MediaRequest);

    public bool WaitsForLibraryRefresh => false;

    public async Task RefreshAsync(DateTimeOffset staleBefore, CancellationToken ct)
    {
        var open = await db.MediaRequests
            .Where(request => OpenStates.Contains(request.State) && request.Title != null)
            .Select(request => new { request.Id, request.Kind, request.TmdbId, request.TitleFetchedAt })
            .ToListAsync(ct);
        foreach (var request in open.Where(request => request.TitleFetchedAt is null || request.TitleFetchedAt < staleBefore))
        {
            if (await titles.FetchNameAsync(request.Kind, request.TmdbId, ct) is string name)
            {
                DateTimeOffset now = time.GetUtcNow();
                await db.MediaRequests.Where(row => row.Id == request.Id).ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.Title, name).SetProperty(row => row.TitleFetchedAt, now),
                    ct);
            }
        }
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct)
    {
        var stored = await db.MediaRequests
            .Where(request => request.Title != null || request.TitleFetchedAt != null)
            .Select(request => new { request.Id, request.TitleFetchedAt })
            .ToListAsync(ct);
        int[] expired = [.. stored.Where(request => request.TitleFetchedAt is null || request.TitleFetchedAt < expiredBefore).Select(request => request.Id)];
        foreach (int[] chunk in expired.Chunk(ChunkSize))
        {
            await Clear(db.MediaRequests.Where(request => chunk.Contains(request.Id)), ct);
        }

        return expired.Length;
    }

    public Task<int> PurgeAllAsync(CancellationToken ct) =>
        Clear(db.MediaRequests.Where(request => request.Title != null || request.TitleFetchedAt != null), ct);

    private static Task<int> Clear(IQueryable<MediaRequest> requests, CancellationToken ct) =>
        requests.ExecuteUpdateAsync(
            setters => setters.SetProperty(request => request.Title, (string?)null).SetProperty(request => request.TitleFetchedAt, (DateTimeOffset?)null),
            ct);
}
