using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Lists;

public static class DiscoveryListEndpoints
{
    public static void MapDiscoveryLists(this RouteGroupBuilder parent)
    {
        parent.MapGet("/settings/discovery-lists", (DiscoveryLists lists, CancellationToken ct) => lists.GetAsync(ct))
            .WithTags("Settings")
            .WithName("GetDiscoveryLists")
            .WithSummary("Gets the configured discovery lists and key status.")
            .Produces<DiscoveryListView>();
        parent.MapPut("/settings/discovery-lists", async (DiscoveryListSettings? input, DiscoveryLists lists, JobRunner jobs, CancellationToken ct) =>
        {
            if (input is null || !await lists.SaveAsync(input, ct))
            {
                return Results.Problem(statusCode: 400, title: "Provide up to five distinct lists with a name, provider and positive list ID.");
            }
            await jobs.StartAsync(DiscoveryLists.JobName, JobRunTrigger.Manual, ct, rerunIfRunning: true);
            return Results.NoContent();
        })
            .WithTags("Settings")
            .WithName("SetDiscoveryLists")
            .WithSummary("Updates discovery lists and starts a refresh.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
