using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Labels;

public sealed class LabelGuardianJob(LabelGuardian guardian, IOptions<LabelGuardianOptions> options, ILogger<LabelGuardianJob> logger) : IBackgroundJob
{
    public const string JobName = "label-guardian";

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await guardian.RunAsync(options.Value.DryRun, ct);
        }
        catch (MediaServerNotConfiguredException exception)
        {
            logger.LogDebug("Label guardian skipped: {Reason}", exception.Message);
        }
    }
}

public sealed class LabelGuardianDryRunJob(LabelGuardian guardian, ILogger<LabelGuardianDryRunJob> logger) : IBackgroundJob
{
    public const string JobName = "label-guardian-dry-run";

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await guardian.RunAsync(dryRun: true, ct);
        }
        catch (MediaServerNotConfiguredException exception)
        {
            logger.LogDebug("Label guardian dry run skipped: {Reason}", exception.Message);
        }
    }
}

public static class LabelGuardianEndpoints
{
    public const int DefaultPage = 20;
    public const int MaxPage = 100;

    public static IServiceCollection AddLabelGuardian(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LabelGuardianOptions>()
            .Bind(configuration.GetSection(LabelGuardianOptions.SectionName))
            .Validate(options => options.IsValid(), "The LabelGuardian configuration section is invalid.")
            .ValidateOnStart();
        services.AddSingleton<LabelGuardianGate>();
        services.AddScoped<LabelGuardian>();
        services.AddBackgroundJob<LabelGuardianJob>(
            LabelGuardianJob.JobName,
            "Compares the label ledger with Plex, follows moved items and puts back owned labels that went missing.",
            TimeSpan.FromDays(1));
        services.AddBackgroundJob<LabelGuardianDryRunJob>(
            LabelGuardianDryRunJob.JobName,
            "Reports label drift like the label guardian without changing Plex or the ledger.",
            null);
        return services;
    }

    public static void MapLabelGuardian(this RouteGroupBuilder v1)
    {
        RouteGroupBuilder guardian = v1.MapGroup("/labels/guardian").WithTags("Labels");

        guardian.MapGet("/reports", async (int? limit, int? offset, WishlistarrDbContext db, CancellationToken ct) =>
        {
            int take = limit ?? DefaultPage;
            int skip = offset ?? 0;
            if (take is < 1 or > MaxPage || skip < 0)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"The limit is 1 to {MaxPage.ToString(CultureInfo.InvariantCulture)} and the offset is not negative.");
            }

            int total = await db.LabelGuardianReports.CountAsync(ct);
            List<LabelGuardianReport> reports = await db.LabelGuardianReports.AsNoTracking()
                .OrderByDescending(report => report.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);
            int[] ids = [.. reports.Select(report => report.Id)];
            var counts = await db.LabelGuardianEntries.AsNoTracking()
                .Where(entry => ids.Contains(entry.ReportId))
                .GroupBy(entry => new { entry.ReportId, entry.Kind, entry.Action })
                .Select(group => new { group.Key.ReportId, group.Key.Kind, group.Key.Action, Count = group.Count() })
                .ToListAsync(ct);
            List<LabelGuardianReportSummary> items = [.. reports.Select(report => Summarize(
                report,
                [.. counts.Where(row => row.ReportId == report.Id).Select(row => (row.Kind, row.Action, row.Count))]))];
            return Results.Ok(new LabelGuardianReportPage(items, total, take, skip));
        })
            .WithName("ListLabelGuardianReports")
            .WithSummary("Lists label guardian reports, newest first. limit (default 20, at most 100) and offset choose the page. Dry runs are marked with dryRun.")
            .Produces<LabelGuardianReportPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        guardian.MapGet("/reports/{id}", async (int id, WishlistarrDbContext db, CancellationToken ct) =>
        {
            LabelGuardianReport? report = await db.LabelGuardianReports.AsNoTracking()
                .Include(item => item.Entries.OrderBy(entry => entry.Id))
                .SingleOrDefaultAsync(item => item.Id == id, ct);
            if (report is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The report does not exist.");
            }

            int[] profileIds = [.. report.Entries.Select(entry => entry.KidProfileId).Distinct()];
            Dictionary<int, string> names = await db.KidProfiles.AsNoTracking()
                .Where(profile => profileIds.Contains(profile.Id))
                .ToDictionaryAsync(profile => profile.Id, profile => profile.Name, ct);
            List<LabelGuardianProfileSummary> profiles = [.. report.Entries
                .GroupBy(entry => entry.KidProfileId)
                .OrderBy(group => group.Key)
                .Select(group => new LabelGuardianProfileSummary(
                    group.Key,
                    names.GetValueOrDefault(group.Key),
                    group.Count(entry => entry.Kind == LabelDriftKind.Missing),
                    group.Count(entry => entry.Kind == LabelDriftKind.Extra),
                    group.Count(entry => entry.Kind == LabelDriftKind.Moved),
                    group.Count(entry => entry.Kind == LabelDriftKind.Unresolved),
                    group.Count(entry => entry.Action == LabelDriftAction.Repaired)))];
            List<LabelGuardianEntryResponse> entries = [.. report.Entries.Select(entry => new LabelGuardianEntryResponse(
                entry.Id,
                entry.KidProfileId,
                names.GetValueOrDefault(entry.KidProfileId),
                entry.Kind,
                entry.Action,
                entry.Label,
                entry.Guid,
                entry.RatingKey,
                entry.PreviousRatingKey,
                entry.PreviousGuid,
                entry.Title,
                entry.Detail))];
            LabelGuardianReportSummary summary = Summarize(report, [.. report.Entries.GroupBy(entry => (entry.Kind, entry.Action)).Select(group => (group.Key.Kind, group.Key.Action, group.Count()))]);
            return Results.Ok(new LabelGuardianReportResponse(summary, profiles, entries));
        })
            .WithName("GetLabelGuardianReport")
            .WithSummary("Returns one label guardian report with its findings per profile: missing, extra, moved and unresolved labels and what was done about each.")
            .Produces<LabelGuardianReportResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static LabelGuardianReportSummary Summarize(LabelGuardianReport report, IReadOnlyList<(LabelDriftKind Kind, LabelDriftAction Action, int Count)> counts) => new(
        report.Id,
        report.StartedAt,
        report.FinishedAt,
        report.DryRun,
        report.LedgerChecked,
        report.ExtrasChecked,
        counts.Where(row => row.Kind == LabelDriftKind.Missing).Sum(row => row.Count),
        counts.Where(row => row.Kind == LabelDriftKind.Extra).Sum(row => row.Count),
        counts.Where(row => row.Kind == LabelDriftKind.Moved).Sum(row => row.Count),
        counts.Where(row => row.Kind == LabelDriftKind.Unresolved).Sum(row => row.Count),
        counts.Where(row => row.Action == LabelDriftAction.Repaired).Sum(row => row.Count),
        counts.Where(row => row.Action == LabelDriftAction.Failed).Sum(row => row.Count));
}

public sealed record LabelGuardianReportSummary(
    int Id,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool DryRun,
    int LedgerChecked,
    bool ExtrasChecked,
    int Missing,
    int Extra,
    int Moved,
    int Unresolved,
    int Repaired,
    int Failed);

public sealed record LabelGuardianReportPage(IReadOnlyList<LabelGuardianReportSummary> Items, int Total, int Limit, int Offset);

public sealed record LabelGuardianProfileSummary(int KidProfileId, string? ProfileName, int Missing, int Extra, int Moved, int Unresolved, int Repaired);

public sealed record LabelGuardianEntryResponse(
    int Id,
    int KidProfileId,
    string? ProfileName,
    LabelDriftKind Kind,
    LabelDriftAction Action,
    string Label,
    string Guid,
    string? RatingKey,
    string? PreviousRatingKey,
    string? PreviousGuid,
    string? Title,
    string? Detail);

public sealed record LabelGuardianReportResponse(
    LabelGuardianReportSummary Report,
    IReadOnlyList<LabelGuardianProfileSummary> Profiles,
    IReadOnlyList<LabelGuardianEntryResponse> Entries);
