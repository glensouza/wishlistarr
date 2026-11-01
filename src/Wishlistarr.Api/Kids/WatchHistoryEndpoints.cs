namespace Wishlistarr.Api.Kids;

public static class WatchHistoryEndpoints
{
    public static void MapWatchHistory(this RouteGroupBuilder parent)
    {
        parent.MapGet("/kid-profiles/{id}/watch-history", async (int id, WatchHistory history, HttpContext context, CancellationToken ct) =>
        {
            ProfileWatchState? state = await history.ReadAsync(id, ct);
            if (state is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(state);
        })
            .WithTags("Kid profiles")
            .WithName("GetProfileWatchHistory")
            .WithSummary("Returns observed watch history for a profile. Missing titles have unknown state.")
            .Produces<ProfileWatchState>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
