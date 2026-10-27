using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Labels;

public sealed class LabelGuardianOptions
{
    public const string SectionName = "LabelGuardian";

    public bool DryRun { get; set; }

    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(180);

    public bool IsValid() => this.HistoryRetention > TimeSpan.Zero;
}

public sealed class LabelGuardianGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        return new Releaser(this.gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

public sealed partial class LabelGuardian(
    WishlistarrDbContext db,
    IMediaServer server,
    ILibraryScanner scanner,
    LabelWriter labels,
    LabelGuardianGate gate,
    IOptions<LabelGuardianOptions> options,
    IOptions<ApprovalOptions> approval,
    TimeProvider time,
    ILogger<LabelGuardian> logger)
{
    public const string NotFoundDetail = "No item with this guid or its external ids was found in Plex.";
    public const string AmbiguousDetail = "The external ids match more than one Plex item, so the guardian cannot tell which one it is.";
    public const string ConflictDetail = "Another ledger entry already owns this label on the item's new guid.";
    public const string LedgerChangedDetail = "The ledger entry changed while the guardian was running.";
    public const string LedgerFailedDetail = "The ledger entry could not be updated.";
    public const string NotOwnedDetail = "The ledger no longer owns this label for an active kid profile.";
    public const string ItemGoneDetail = "The item is no longer in Plex.";
    public const string TimeoutDetail = "Plex did not answer in time, so the label may or may not be on the item.";
    public const string UnreachableDetail = "Plex could not be reached, so the repair was not tried.";
    public const string StoppedDetail = "The application was stopping, so the repair was not tried.";
    public const string InterruptedDetail = "The application stopped while the label was being written, so it may or may not be on the item.";
    public const string AbortedDetail = "The guardian stopped after an unexpected error, so this was not tried.";

    private const int SqliteConstraint = 19;

    private readonly Dictionary<string, MediaItemLabels?> details = new(StringComparer.Ordinal);

    // A dry run started by a parent never prunes, so it changes nothing. The scheduled job prunes even when it is configured to run dry,
    // because otherwise history would never be pruned while it does.
    public async Task<LabelGuardianReport> RunAsync(bool dryRun, bool pruneHistory, CancellationToken ct)
    {
        using IDisposable held = await gate.EnterAsync(ct);
        this.details.Clear();
        DateTimeOffset started = time.GetUtcNow();

        // Everything is read before anything is written, so a Plex failure part way through ends the run with no drift recorded and nothing repaired.
        LibraryLabelScan scan = await scanner.ScanLabelsAsync(ct);
        string serverId = scan.ServerId;
        List<KidProfile> profiles = await db.KidProfiles.AsNoTracking()
            .Where(profile => profile.ArchivedAt == null)
            .OrderBy(profile => profile.Id)
            .ToListAsync(ct);
        int[] active = [.. profiles.Select(profile => profile.Id)];
        List<OwnedLabel> rows = await db.OwnedLabels.AsNoTracking()
            .Where(row => row.ServerId == serverId && active.Contains(row.KidProfileId))
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        Dictionary<string, HashSet<string>> owned = await this.LoadOwnedAsync(serverId, ct);

        List<PlannedRow> plans = [];
        foreach (OwnedLabel row in rows)
        {
            plans.Add(await this.PlanAsync(row, serverId, owned, dryRun, ct));
        }

        List<LabelGuardianEntry> extras = scan.Complete
            ? await this.FindExtrasAsync(scan, profiles, owned, ct)
            : [];

        // Once repairs start, the report is saved even if the application is stopping or a write fails unexpectedly, so a repair that was made is never missing from the history.
        ExceptionDispatchInfo? failure = null;
        if (!dryRun)
        {
            try
            {
                await this.ApplyAsync(plans, ct);
            }
            catch (Exception ex)
            {
                this.LogApplyFailed(ex);
                failure = ExceptionDispatchInfo.Capture(ex);
                foreach (LabelGuardianEntry untried in plans
                    .SelectMany(plan => plan.Move is null ? plan.Repairs : [plan.Move, .. plan.Repairs])
                    .Where(entry => entry.Action == LabelDriftAction.None))
                {
                    untried.Action = LabelDriftAction.Skipped;
                    untried.Detail = AbortedDetail;
                }
            }
        }

        LabelGuardianReport report = new()
        {
            StartedAt = started,
            DryRun = dryRun,
            ServerId = serverId,
            LedgerChecked = rows.Count,
            ExtrasChecked = scan.Complete,
            Entries = [.. plans.SelectMany(plan => plan.Entries), .. extras],
        };
        report.FinishedAt = time.GetUtcNow();
        db.LabelGuardianReports.Add(report);
        await db.SaveChangesAsync(CancellationToken.None);
        this.LogFinished(report.Id, dryRun, report.Entries.Count);

        if (pruneHistory)
        {
            DateTimeOffset cutoff = report.FinishedAt - options.Value.HistoryRetention;
            List<int> expired = [.. (await db.LabelGuardianReports.AsNoTracking()
                .Select(item => new { item.Id, item.StartedAt })
                .ToListAsync(CancellationToken.None))
                .Where(item => item.StartedAt < cutoff && item.Id != report.Id)
                .Select(item => item.Id)];
            await db.LabelGuardianEntries.Where(entry => expired.Contains(entry.ReportId)).ExecuteDeleteAsync(CancellationToken.None);
            await db.LabelGuardianReports.Where(item => expired.Contains(item.Id)).ExecuteDeleteAsync(CancellationToken.None);
        }

        failure?.Throw();
        ct.ThrowIfCancellationRequested();
        return report;
    }

    private static string Truncate(string? value, int length) =>
        value is null ? string.Empty : value.Length > length ? value[..length] : value;

    private static bool Owns(Dictionary<string, HashSet<string>> owned, string guid, string label) =>
        owned.TryGetValue(guid, out HashSet<string>? held) && held.Contains(label);

    // Ledger labels are compared with the database's collation, so this agrees with the label writer's ownership query.
    private static void Own(Dictionary<string, HashSet<string>> owned, string guid, string label)
    {
        if (!owned.TryGetValue(guid, out HashSet<string>? held))
        {
            owned[guid] = held = new HashSet<string>(LabelCollation.Comparer);
        }

        held.Add(label);
    }

    private static ExternalIdSource ToExternalSource(LibraryIdSource source) => source switch
    {
        LibraryIdSource.Tmdb => ExternalIdSource.Tmdb,
        LibraryIdSource.Tvdb => ExternalIdSource.Tvdb,
        _ => ExternalIdSource.Imdb,
    };

    // Sharing one id is not enough on its own: an id from a source where the item disagrees with every known id means it describes another title.
    private static bool IsSameTitle(MediaItemLabels item, IReadOnlyList<KnownId> known)
    {
        KnownId[] ofKind = [.. known.Where(id => id.Kind == item.Kind)];
        bool IsKnown(ExternalId id) => ofKind.Any(other => other.Source == id.Source && other.Value == id.Value);
        return item.ExternalIds.Any(IsKnown)
            && item.ExternalIds
                .GroupBy(id => id.Source)
                .All(group => group.Any(IsKnown) || ofKind.All(other => other.Source != group.Key));
    }

    private static bool IsUniqueViolation(Exception ex) =>
        ex is SqliteException { SqliteErrorCode: SqliteConstraint } || ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint };

    private async Task<Dictionary<string, HashSet<string>>> LoadOwnedAsync(string serverId, CancellationToken ct)
    {
        var all = await db.OwnedLabels.AsNoTracking()
            .Where(row => row.ServerId == serverId)
            .Select(row => new { row.Guid, row.Label })
            .ToListAsync(ct);
        Dictionary<string, HashSet<string>> owned = new(StringComparer.Ordinal);
        foreach (var row in all)
        {
            Own(owned, row.Guid, row.Label);
        }

        return owned;
    }

    private async Task<MediaItemLabels?> DetailAsync(string ratingKey, CancellationToken ct)
    {
        if (!this.details.TryGetValue(ratingKey, out MediaItemLabels? item))
        {
            item = await server.GetLabelsAsync(ratingKey, ct);
            this.details[ratingKey] = item;
        }

        return item;
    }

    private async Task<List<MediaItemLabels>> ConfirmAsync(IEnumerable<string> ratingKeys, string serverId, Func<MediaItemLabels, bool> accept, CancellationToken ct)
    {
        List<MediaItemLabels> confirmed = [];
        foreach (string key in ratingKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (await this.DetailAsync(key, ct) is MediaItemLabels item
                && item.ServerId == serverId
                && !string.IsNullOrEmpty(item.Guid)
                && accept(item)
                && confirmed.All(existing => existing.Id != item.Id))
            {
                confirmed.Add(item);
            }
        }

        return confirmed;
    }

    // An item added again gets a new rating key, while its guid comes from the metadata match, so copies are found by guid first.
    private async Task<(List<MediaItemLabels> Items, string? Detail)> ResolveAsync(OwnedLabel row, string serverId, CancellationToken ct)
    {
        List<string> keys = [row.RatingKey];
        keys.AddRange(await db.LibraryItems.AsNoTracking()
            .Where(item => item.ServerId == serverId && item.Guid == row.Guid)
            .Select(item => item.RatingKey)
            .ToListAsync(ct));
        List<MediaItemLabels> same = await this.ConfirmAsync(keys, serverId, item => item.Guid == row.Guid, ct);
        if (same.Count == 0)
        {
            // Asking Plex costs a request per library, so it is only done when neither the stored key nor the index finds the guid.
            same = await this.ConfirmAsync(
                (await server.FindByGuidAsync(row.Guid, ct)).Select(item => item.Id),
                serverId,
                item => item.Guid == row.Guid,
                ct);
        }

        if (same.Count > 0)
        {
            return (same, null);
        }

        // A changed match gives the item a new guid, while the library index may still hold the external ids it had under the ledger's guid.
        // Only index rows that still carry the ledger's guid describe the labelled title: a Fix Match keeps the rating key, so the index row with that key
        // may already describe a different title. A candidate must show one of those ids in Plex's own answer, and exactly one candidate must remain.
        var indexed = await db.LibraryExternalIds.AsNoTracking()
            .Where(id => id.Item!.ServerId == serverId
                && id.Item.Guid == row.Guid
                && id.Origin == LibraryIdOrigin.Plex
                && (id.Source == LibraryIdSource.Tmdb || id.Source == LibraryIdSource.Tvdb || id.Source == LibraryIdSource.Imdb))
            .Select(id => new { id.Source, id.Value, id.Item!.Kind })
            .Distinct()
            .ToListAsync(ct);
        if (indexed.Count == 0)
        {
            return ([], NotFoundDetail);
        }

        List<string> candidates = [];
        foreach (var id in indexed)
        {
            candidates.AddRange(await db.LibraryExternalIds.AsNoTracking()
                .Where(other => other.Source == id.Source && other.Value == id.Value
                    && other.Item!.ServerId == serverId && other.Item.Kind == id.Kind)
                .Select(other => other.Item!.RatingKey)
                .ToListAsync(ct));
        }

        KnownId[] known = [.. indexed.Select(id => new KnownId(ToExternalSource(id.Source), id.Value, id.Kind))];
        List<MediaItemLabels> moved = await this.ConfirmAsync(candidates, serverId, item => IsSameTitle(item, known), ct);
        return moved.Count switch
        {
            0 => ([], NotFoundDetail),
            1 => (moved, null),
            _ => ([], AmbiguousDetail),
        };
    }

    private async Task<PlannedRow> PlanAsync(OwnedLabel row, string serverId, Dictionary<string, HashSet<string>> owned, bool dryRun, CancellationToken ct)
    {
        PlannedRow plan = new(row);
        (List<MediaItemLabels> items, string? failure) = await this.ResolveAsync(row, serverId, ct);
        if (items.Count == 0)
        {
            plan.Entries.Add(this.Entry(row, LabelDriftKind.Unresolved, LabelDriftAction.None, row.Guid, row.RatingKey, failure));
            return plan;
        }

        string guid = items[0].Guid;
        if (guid != row.Guid && Owns(owned, guid, row.Label))
        {
            plan.Entries.Add(this.Entry(row, LabelDriftKind.Unresolved, LabelDriftAction.None, row.Guid, row.RatingKey, ConflictDetail));
            return plan;
        }

        if (guid != row.Guid || items.All(item => item.Id != row.RatingKey))
        {
            MediaItemLabels target = items.FirstOrDefault(item => item.Id == row.RatingKey) ?? items[0];
            LabelGuardianEntry entry = this.Entry(row, LabelDriftKind.Moved, dryRun ? LabelDriftAction.WouldUpdate : LabelDriftAction.None, guid, target.Id, null, target.Title);
            entry.PreviousRatingKey = row.RatingKey;
            entry.PreviousGuid = guid == row.Guid ? null : row.Guid;
            plan.Move = entry;
            plan.Entries.Add(entry);
            Own(owned, guid, row.Label);
        }

        foreach (MediaItemLabels item in items.Where(item => !item.Labels.Contains(row.Label, LabelWriter.LabelComparer)))
        {
            LabelGuardianEntry entry = this.Entry(row, LabelDriftKind.Missing, dryRun ? LabelDriftAction.WouldRepair : LabelDriftAction.None, guid, item.Id, null, item.Title);
            plan.Repairs.Add(entry);
            plan.Entries.Add(entry);
        }

        return plan;
    }

    private async Task<List<LabelGuardianEntry>> FindExtrasAsync(
        LibraryLabelScan scan,
        IReadOnlyList<KidProfile> profiles,
        Dictionary<string, HashSet<string>> owned,
        CancellationToken ct)
    {
        List<LabelGuardianEntry> extras = [];
        foreach (KidProfile profile in profiles)
        {
            string[] keys = [.. scan.LabelsByItem
                .Where(pair => pair.Value.Contains(profile.PlexLabel, LabelWriter.LabelComparer))
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)];

            // The guid comes from what Plex answered during this run, never from the library index, which can lag behind a changed match.
            foreach (string key in keys)
            {
                (string Guid, string Title) found;
                if (this.details.TryGetValue(key, out MediaItemLabels? seen) && seen is not null)
                {
                    found = (seen.Guid, seen.Title);
                }
                else if (scan.Items.TryGetValue(key, out LabeledItem? listed) && !string.IsNullOrEmpty(listed.Guid))
                {
                    found = (listed.Guid, listed.Title);
                }
                else
                {
                    if (await this.DetailAsync(key, ct) is not MediaItemLabels item)
                    {
                        continue;
                    }

                    found = (item.Guid, item.Title);
                }

                if (!Owns(owned, found.Guid, profile.PlexLabel))
                {
                    extras.Add(new LabelGuardianEntry
                    {
                        KidProfileId = profile.Id,
                        Kind = LabelDriftKind.Extra,
                        Action = LabelDriftAction.None,
                        Label = profile.PlexLabel,
                        Guid = Truncate(found.Guid, 256),
                        RatingKey = key,
                        Title = Truncate(found.Title, LabelGuardianEntry.MaxTitleLength),
                    });
                }
            }
        }

        return extras;
    }

    private async Task ApplyAsync(IReadOnlyList<PlannedRow> plans, CancellationToken ct)
    {
        bool unreachable = false;
        foreach (PlannedRow plan in plans)
        {
            if (plan.Move is LabelGuardianEntry move && !await this.MoveAsync(plan.Row, move))
            {
                foreach (LabelGuardianEntry skipped in plan.Repairs)
                {
                    skipped.Action = LabelDriftAction.Skipped;
                    skipped.Detail = LedgerChangedDetail;
                }

                continue;
            }

            foreach (LabelGuardianEntry repair in plan.Repairs)
            {
                if (unreachable || ct.IsCancellationRequested)
                {
                    repair.Action = LabelDriftAction.Skipped;
                    repair.Detail = ct.IsCancellationRequested ? StoppedDetail : UnreachableDetail;
                    continue;
                }

                unreachable = await this.RepairAsync(plan.Row, repair, ct);
            }
        }
    }

    private async Task<bool> MoveAsync(OwnedLabel row, LabelGuardianEntry move)
    {
        try
        {
            int changed = await db.OwnedLabels
                .Where(owned => owned.Id == row.Id && owned.Guid == row.Guid && owned.RatingKey == row.RatingKey)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(owned => owned.Guid, move.Guid)
                        .SetProperty(owned => owned.RatingKey, move.RatingKey!),
                    CancellationToken.None);
            if (changed == 0)
            {
                move.Action = LabelDriftAction.Skipped;
                move.Detail = LedgerChangedDetail;
                return false;
            }

            move.Action = LabelDriftAction.Updated;
            return true;
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            // ExecuteUpdateAsync reports a unique index violation as the provider's exception, not as a DbUpdateException.
            this.LogMoveFailed(ex, row.Id);
            move.Action = LabelDriftAction.Failed;
            move.Detail = IsUniqueViolation(ex) ? ConflictDetail : LedgerFailedDetail;
            return false;
        }
    }

    // Returns true when Plex stopped answering, so the remaining repairs are not tried against a server that is down.
    private async Task<bool> RepairAsync(OwnedLabel row, LabelGuardianEntry repair, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(approval.Value.LabelTimeout);
        try
        {
            LabelWriteResult result = await labels.RestoreAsync(repair.RatingKey!, row.Label, deadline.Token);
            (repair.Action, repair.Detail) = result.Outcome switch
            {
                LabelWriteOutcome.Added => (LabelDriftAction.Repaired, (string?)null),
                LabelWriteOutcome.AlreadyPresent => (LabelDriftAction.AlreadyFixed, null),
                LabelWriteOutcome.NotOwned => (LabelDriftAction.Skipped, NotOwnedDetail),
                _ => (LabelDriftAction.Failed, ItemGoneDetail),
            };
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The write may already have reached Plex, so the entry must not claim that nothing was tried.
            repair.Action = LabelDriftAction.Interrupted;
            repair.Detail = InterruptedDetail;
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            this.LogRepairFailed(ex, row.Id, repair.RatingKey!);
            repair.Action = LabelDriftAction.Failed;
            repair.Detail = ex is OperationCanceledException ? TimeoutDetail : UnreachableDetail;
            return true;
        }
        catch (MediaServerException ex)
        {
            this.LogRepairFailed(ex, row.Id, repair.RatingKey!);
            repair.Action = LabelDriftAction.Failed;
            repair.Detail = Truncate(ex.Message, LabelGuardianEntry.MaxDetailLength);
            return ex is MediaServerNotConfiguredException || (int?)ex.StatusCode >= 500;
        }
        catch
        {
            repair.Action = LabelDriftAction.Interrupted;
            repair.Detail = InterruptedDetail;
            throw;
        }
    }

    private LabelGuardianEntry Entry(OwnedLabel row, LabelDriftKind kind, LabelDriftAction action, string guid, string? ratingKey, string? detail, string? title = null) => new()
    {
        KidProfileId = row.KidProfileId,
        Kind = kind,
        Action = action,
        Label = row.Label,
        Guid = Truncate(guid, 256),
        RatingKey = ratingKey,
        Title = title is null ? null : Truncate(title, LabelGuardianEntry.MaxTitleLength),
        Detail = detail,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Label guardian report {ReportId} finished (dry run: {DryRun}) with {Entries} findings")]
    private partial void LogFinished(int reportId, bool dryRun, int entries);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label guardian could not move ledger entry {LedgerId}")]
    private partial void LogMoveFailed(Exception ex, int ledgerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label guardian could not repair ledger entry {LedgerId} on item {ItemId}")]
    private partial void LogRepairFailed(Exception ex, int ledgerId, string itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Label guardian stopped repairing after an unexpected error")]
    private partial void LogApplyFailed(Exception ex);

    private sealed record KnownId(ExternalIdSource Source, string Value, MediaKind Kind);

    private sealed class PlannedRow(OwnedLabel row)
    {
        public OwnedLabel Row { get; } = row;

        public LabelGuardianEntry? Move { get; set; }

        public List<LabelGuardianEntry> Repairs { get; } = [];

        public List<LabelGuardianEntry> Entries { get; } = [];
    }
}
