using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.System;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public enum RequestOutcome
{
    Created,
    Merged,
    Changed,
    Unchanged,
    Rejected,
    NotFound,
}

public sealed record RequestResult(RequestOutcome Outcome, MediaRequest? Request = null, string? Error = null);

public sealed class MediaRequests(WishlistarrDbContext db, TimeProvider time)
{
    public const int MaxTitleLength = 300;
    public const int MaxReasonLength = 500;
    public const int MaxAttempts = 5;

    private const int SqliteConstraintUnique = 2067;

    public async Task<RequestResult> CreateAsync(
        int kidProfileId,
        TitleKey title,
        string titleName,
        RequestActor actor,
        bool countsTowardAllowance = false,
        CancellationToken ct = default,
        string? reason = null,
        bool mergeExisting = true)
    {
        ValidateActor(actor);
        ValidateReason(reason);
        if (title.TmdbId <= 0)
        {
            throw new ArgumentException("TMDB ids are positive.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(titleName) || titleName.Length > MaxTitleLength)
        {
            throw new ArgumentException($"Titles are 1 to {MaxTitleLength} characters.", nameof(titleName));
        }

        if (actor.Kind == RequestActorKind.Kid && actor.Id != kidProfileId)
        {
            return new RequestResult(RequestOutcome.Rejected, Error: "Kids can only request titles for their own profile.");
        }

        if (!await db.KidProfiles.AnyAsync(profile => profile.Id == kidProfileId && profile.ArchivedAt == null, ct))
        {
            return new RequestResult(RequestOutcome.Rejected, Error: "The kid profile does not exist.");
        }

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            DateTimeOffset now = time.GetUtcNow();
            MediaRequest? existing = await db.MediaRequests.FirstOrDefaultAsync(
                request => request.KidProfileId == kidProfileId
                    && request.Kind == title.Kind
                    && request.TmdbId == title.TmdbId
                    && request.State != RequestState.Denied
                    && request.State != RequestState.Revoked,
                ct);
            if (existing is not null && !mergeExisting)
            {
                return new RequestResult(RequestOutcome.Unchanged, existing);
            }

            if (existing is not null)
            {
                int merged = await db.MediaRequests
                    .Where(request => request.Id == existing.Id
                        && request.State != RequestState.Denied
                        && request.State != RequestState.Revoked)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(request => request.RequestCount, request => request.RequestCount + 1)
                            .SetProperty(request => request.LastRequestedAt, now)
                            .SetProperty(request => request.Title, titleName.Trim())
                            .SetProperty(request => request.TitleFetchedAt, now),
                        ct);
                await db.Entry(existing).ReloadAsync(ct);
                if (merged == 1)
                {
                    return new RequestResult(RequestOutcome.Merged, existing);
                }

                continue;
            }

            MediaRequest created = new()
            {
                KidProfileId = kidProfileId,
                Kind = title.Kind,
                TmdbId = title.TmdbId,
                Title = titleName.Trim(),
                TitleFetchedAt = now,
                State = RequestState.Pending,
                RequestCount = 1,
                CountsTowardAllowance = countsTowardAllowance,
                CreatedAt = now,
                LastRequestedAt = now,
                StateChangedAt = now,
            };
            MediaRequestEvent item = NewEvent(null, RequestState.Pending, actor, reason, now);
            created.Events.Add(item);
            db.MediaRequests.Add(created);
            try
            {
                await db.SaveChangesAsync(ct);
                AppMetrics.RecordRequest(created.Kind, RequestState.Pending);
                return new RequestResult(RequestOutcome.Created, created);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique })
            {
                db.Entry(item).State = EntityState.Detached;
                db.Entry(created).State = EntityState.Detached;
            }
        }

        throw new DbUpdateConcurrencyException("The request kept changing while it was being saved.");
    }

    public async Task<RequestResult> TransitionAsync(
        int requestId,
        RequestState to,
        RequestActor actor,
        string? reason = null,
        CancellationToken ct = default,
        RequestState? expectedFrom = null)
    {
        ValidateActor(actor);
        ValidateReason(reason);

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            MediaRequest? request = await this.LoadFreshAsync(requestId, ct);
            if (request is null)
            {
                return new RequestResult(RequestOutcome.NotFound, Error: "Request not found.");
            }

            if (request.State == to)
            {
                return new RequestResult(RequestOutcome.Unchanged, request);
            }

            if (expectedFrom is RequestState expected && request.State != expected)
            {
                return new RequestResult(RequestOutcome.Rejected, request, $"The request is {request.State}, not {expected}.");
            }

            if (RequestStateMachine.Find(request.State, to) is not RequestTransition transition)
            {
                return new RequestResult(RequestOutcome.Rejected, request, $"A request cannot move from {request.State} to {to}.");
            }

            if (!transition.Actors.Contains(actor.Kind))
            {
                return new RequestResult(RequestOutcome.Rejected, request, $"A {actor.Kind} cannot move a request from {request.State} to {to}.");
            }

            DateTimeOffset now = time.GetUtcNow();
            MediaRequestEvent item = NewEvent(request.State, to, actor, reason, now);
            item.MediaRequestId = request.Id;
            request.State = to;
            request.StateChangedAt = now;
            request.Version++;
            db.MediaRequestEvents.Add(item);
            try
            {
                await db.SaveChangesAsync(ct);
                AppMetrics.RecordRequest(request.Kind, to);
                return new RequestResult(RequestOutcome.Changed, request);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(item).State = EntityState.Detached;
                db.Entry(request).State = EntityState.Detached;
            }
        }

        throw new DbUpdateConcurrencyException("The request kept changing while it was being saved.");
    }

    public async Task<RequestResult> RetryAsync(
        int requestId,
        RequestActor actor,
        string? reason = null,
        CancellationToken ct = default)
    {
        MediaRequest? request = await db.MediaRequests.AsNoTracking().FirstOrDefaultAsync(row => row.Id == requestId, ct);
        if (request is null)
        {
            return new RequestResult(RequestOutcome.NotFound, Error: "Request not found.");
        }

        MediaRequestEvent? failure = await db.MediaRequestEvents.AsNoTracking()
            .Where(item => item.MediaRequestId == requestId && item.ToState == RequestState.Failed)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(ct);
        if (failure?.FromState is not RequestState failedFrom)
        {
            return new RequestResult(RequestOutcome.Rejected, request, "Only failed requests can be retried.");
        }

        RequestState target = RequestStateMachine.RetryTargetFor(failedFrom);
        if (request.State != RequestState.Failed && request.State != target)
        {
            return new RequestResult(RequestOutcome.Rejected, request, "Only failed requests can be retried.");
        }

        return await this.TransitionAsync(requestId, target, actor, reason, ct);
    }

    public Task<MediaRequest?> GetAsync(int requestId, CancellationToken ct = default) =>
        this.LoadFreshAsync(requestId, ct);

    public async Task<IReadOnlyList<MediaRequestEvent>> GetHistoryAsync(int requestId, CancellationToken ct = default) =>
        await db.MediaRequestEvents.AsNoTracking()
            .Where(item => item.MediaRequestId == requestId)
            .OrderBy(item => item.Id)
            .ToListAsync(ct);

    private async Task<MediaRequest?> LoadFreshAsync(int requestId, CancellationToken ct)
    {
        if (db.MediaRequests.Local.FirstOrDefault(row => row.Id == requestId) is not MediaRequest tracked)
        {
            return await db.MediaRequests.FirstOrDefaultAsync(row => row.Id == requestId, ct);
        }

        // A tracked copy is not refreshed by a query, so it could hold a state another writer has already moved on from.
        await db.Entry(tracked).ReloadAsync(ct);
        return db.Entry(tracked).State == EntityState.Detached ? null : tracked;
    }

    private static MediaRequestEvent NewEvent(RequestState? from, RequestState to, RequestActor actor, string? reason, DateTimeOffset now) => new()
    {
        FromState = from,
        ToState = to,
        ActorKind = actor.Kind,
        ActorId = actor.Id,
        ActorName = actor.Name,
        Reason = reason,
        OccurredAt = now,
    };

    private static void ValidateActor(RequestActor actor)
    {
        if (string.IsNullOrWhiteSpace(actor.Name) || actor.Name.Length > RequestActor.MaxNameLength)
        {
            throw new ArgumentException($"Actor names are 1 to {RequestActor.MaxNameLength} characters.", nameof(actor));
        }

        if (actor.Kind == RequestActorKind.Kid && actor.Id is null)
        {
            throw new ArgumentException("A kid actor needs a profile id.", nameof(actor));
        }
    }

    private static void ValidateReason(string? reason)
    {
        if (reason?.Length > MaxReasonLength)
        {
            throw new ArgumentException($"Reasons are limited to {MaxReasonLength} characters.", nameof(reason));
        }
    }
}
