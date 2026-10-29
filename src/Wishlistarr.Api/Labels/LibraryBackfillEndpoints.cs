using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Labels;

public static class LibraryBackfillEndpoints
{
    public static IServiceCollection AddLibraryBackfill(this IServiceCollection services)
    {
        services.AddSingleton<BackfillGate>();
        services.AddScoped<LibraryBackfill>();
        services.AddBackgroundJob<LibraryBackfillJob>(LibraryBackfillJob.JobName, "Applies or undoes up to 25 selected library labels, resuming persisted progress after a restart.", TimeSpan.FromSeconds(30));
        return services;
    }

    public static void MapLibraryBackfill(this RouteGroupBuilder v1)
    {
        RouteGroupBuilder group = v1.MapGroup("/labels/backfill").WithTags("Labels");
        group.MapGet("/preview", (int kidProfileId, int? offset, LibraryBackfill backfill, CancellationToken ct) =>
            ExecuteAsync(async () => Results.Ok(await backfill.PreviewAsync(kidProfileId, offset ?? 0, ct))))
            .WithName("PreviewLibraryBackfill")
            .WithSummary("Evaluates a page of the indexed library against a profile's content policy without changing Plex or the database.")
            .Produces<BackfillPreview>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(502);

        group.MapPost("/batches", (BackfillApply input, LibraryBackfill backfill, JobRunner jobs, CancellationToken ct) =>
            ExecuteAsync(async () =>
            {
                BackfillBatchResponse? batch = await backfill.CreateAsync(input, ct);
                if (batch is null) return Results.Ok(new { dryRun = true, selected = input.Items.Count });
                await jobs.StartAsync(LibraryBackfillJob.JobName, JobRunTrigger.Manual, CancellationToken.None, rerunIfRunning: true);
                return Results.Accepted($"/api/v1/labels/backfill/batches/{batch.Id}", batch);
            }))
            .WithName("ApplyLibraryBackfill")
            .WithSummary("Queues the parent's selected titles as a resumable batch. dryRun validates the selection without creating a batch or changing labels.")
            .Produces<BackfillBatchResponse>(202).Produces(200)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(502);

        group.MapGet("/batches", async (WishlistarrDbContext db, CancellationToken ct) =>
        {
            List<LibraryBackfillBatch> batches = await db.LibraryBackfillBatches.AsNoTracking().Include(batch => batch.Items).OrderByDescending(batch => batch.Id).Take(50).ToListAsync(ct);
            return Results.Ok(batches.Select(LibraryBackfill.Summarize));
        })
            .WithName("ListLibraryBackfills")
            .WithSummary("Lists the latest 50 library backfill batches and their durable progress.")
            .Produces<IReadOnlyList<BackfillBatchResponse>>();

        group.MapGet("/batches/{id}", async (int id, WishlistarrDbContext db, CancellationToken ct) =>
        {
            LibraryBackfillBatch? batch = await db.LibraryBackfillBatches.AsNoTracking().Include(batch => batch.Items.OrderBy(item => item.Id)).SingleOrDefaultAsync(batch => batch.Id == id, ct);
            return batch is null
                ? Results.Problem(statusCode: 404, title: "The backfill batch does not exist.")
                : Results.Ok(new BackfillBatchDetail(LibraryBackfill.Summarize(batch), [.. batch.Items.Select(item => new BackfillItemResponse(item.Title, item.Kind, item.TmdbId, item.State, item.Detail))]));
        })
            .WithName("GetLibraryBackfill")
            .WithSummary("Returns a batch's progress and the result for each selected Plex copy.")
            .Produces<BackfillBatchDetail>().ProducesProblem(404);

        group.MapPost("/batches/{id}/resume", (int id, LibraryBackfill backfill, JobRunner jobs, CancellationToken ct) => ChangeAsync(id, false, backfill, jobs, ct))
            .WithName("ResumeLibraryBackfill")
            .WithSummary("Resumes a paused apply or undo from its saved progress.")
            .Produces<BackfillBatchResponse>(202).ProducesProblem(404);

        group.MapPost("/batches/{id}/undo", (int id, LibraryBackfill backfill, JobRunner jobs, CancellationToken ct) => ChangeAsync(id, true, backfill, jobs, ct))
            .WithName("UndoLibraryBackfill")
            .WithSummary("Queues removal of labels owned by this batch, preserving labels and ownership from outside it.")
            .Produces<BackfillBatchResponse>(202).ProducesProblem(404);
    }

    private static Task<IResult> ChangeAsync(int id, bool undo, LibraryBackfill backfill, JobRunner jobs, CancellationToken ct) => ExecuteAsync(async () =>
    {
        BackfillBatchResponse batch = await backfill.ChangeAsync(id, undo, ct);
        await jobs.StartAsync(LibraryBackfillJob.JobName, JobRunTrigger.Manual, CancellationToken.None, rerunIfRunning: true);
        return Results.Accepted($"/api/v1/labels/backfill/batches/{id}", batch);
    });

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (BackfillException ex) { return Results.Problem(statusCode: ex.Status, title: ex.Message); }
        catch (Exception ex) when (ex is MediaServerNotConfiguredException or TmdbNotConfiguredException)
        {
            return Results.Problem(statusCode: 409, title: "Link Plex, select a server and configure TMDB before previewing the library.");
        }
        catch (Exception ex) when (ex is MediaServerException or TmdbException or HttpRequestException)
        {
            return Results.Problem(statusCode: 502, title: "Plex or TMDB could not be reached. Try again when the service is available.");
        }
    }
}
