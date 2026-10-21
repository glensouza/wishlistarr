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
    public async Task<string?> FetchNameAsync(MediaKind kind, int tmdbId, CancellationToken ct)
    {
        try
        {
            TmdbDetails details = await tmdb.GetDetailsAsync(kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv, tmdbId, ct: ct);
            string name = (details.Title ?? details.Name ?? string.Empty).Trim();
            return name.Length == 0 ? null : name[..Math.Min(name.Length, MediaRequests.MaxTitleLength)];
        }
        catch (TmdbException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string> GetAsync(MediaRequest request, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (request.Title is { Length: > 0 } stored
            && request.TitleFetchedAt is DateTimeOffset fetchedAt
            && fetchedAt >= now - await retention.GetAsync(ct))
        {
            return stored;
        }

        try
        {
            if (await this.FetchNameAsync(request.Kind, request.TmdbId, ct) is not string name)
            {
                return string.Empty;
            }

            await db.MediaRequests.Where(row => row.Id == request.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.Title, name).SetProperty(row => row.TitleFetchedAt, now),
                ct);
            return name;
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
