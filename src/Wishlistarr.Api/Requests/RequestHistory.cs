using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public static class RequestHistory
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public static void MapRequestHistory(this RouteGroupBuilder profiles)
    {
        profiles.MapGet("/{id}/events", async (int id, int? limit, WishlistarrDbContext db, CancellationToken ct) =>
        {
            if (!await db.KidProfiles.AsNoTracking().AnyAsync(profile => profile.Id == id, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Profile not found.");
            }

            int take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            List<RequestEventView> events = await db.MediaRequestEvents.AsNoTracking()
                .Where(item => item.MediaRequest!.KidProfileId == id)
                .OrderByDescending(item => item.Id)
                .Take(take)
                .Select(item => new RequestEventView(
                    item.Id,
                    item.MediaRequestId,
                    item.MediaRequest!.Kind,
                    item.MediaRequest.TmdbId,
                    item.MediaRequest.Title,
                    item.FromState,
                    item.ToState,
                    item.ActorKind,
                    item.ActorName,
                    item.Reason,
                    item.OccurredAt))
                .ToListAsync(ct);
            return Results.Ok(events);
        })
            .WithTags("Kid profiles")
            .WithName("ListKidRequestEvents")
            .WithSummary("Lists request events for one kid profile, newest first. Archived profiles are included.")
            .Produces<IReadOnlyList<RequestEventView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}

public sealed record RequestEventView(
    int Id,
    int RequestId,
    MediaKind Kind,
    int TmdbId,
    string? Title,
    RequestState? FromState,
    RequestState ToState,
    RequestActorKind ActorKind,
    string ActorName,
    string? Reason,
    DateTimeOffset OccurredAt);
