using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Labels;

public sealed class BackfillException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class BackfillGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed record BackfillPreviewItem(int LibraryItemId, string Guid, string Title, MediaKind Kind, int? TmdbId, ContentOutcome Outcome, ContentReason Reason, bool Selectable);

public sealed record BackfillPreview(int KidProfileId, string ServerId, string Label, IReadOnlyList<BackfillPreviewItem> Items, int Total, int Offset, int Limit);

public sealed record BackfillSelection(int LibraryItemId, string Guid);

public sealed record BackfillApply(int KidProfileId, string ServerId, string Label, IReadOnlyList<BackfillSelection> Items, bool DryRun = false);

public sealed record BackfillBatchResponse(int Id, int KidProfileId, string Label, BackfillState State, DateTimeOffset CreatedAt, string? Error, int Total, int Pending, int Applied, int Preserved, int Skipped, int Undone);

public sealed record BackfillItemResponse(string Title, MediaKind Kind, int? TmdbId, BackfillItemState State, string? Detail);

public sealed record BackfillBatchDetail(BackfillBatchResponse Batch, IReadOnlyList<BackfillItemResponse> Items);

public sealed class LibraryBackfill(
    WishlistarrDbContext db,
    ILibraryScanner scanner,
    ITmdbClient tmdb,
    LabelWriter labels,
    BackfillGate gate,
    IProfileLabelGate profileLabels,
    IOptions<ApprovalOptions> options,
    TimeProvider time)
{
    public const int PageSize = 100;
    public const int MaxSelection = 10000;
    public const int ChunkSize = 25;

    public static string Source(int batchId) => $"backfill:{batchId.ToString(CultureInfo.InvariantCulture)}";

    public static string UndoSource(int batchId) => $"undo:{batchId.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryBatchId(string source, out int batchId)
    {
        batchId = 0;
        return source.StartsWith("backfill:", StringComparison.Ordinal)
            && int.TryParse(source.AsSpan("backfill:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out batchId)
            && batchId > 0;
    }

    public static IQueryable<LibraryBackfillItem> RepairableCopies(WishlistarrDbContext db, int batchId) =>
        db.LibraryBackfillItems.Where(item => item.BatchId == batchId && item.OwnsLabel
            && (item.State == BackfillItemState.Pending || item.State == BackfillItemState.Applied)
            && (item.Batch!.State == BackfillState.Applying || item.Batch.State == BackfillState.Paused || item.Batch.State == BackfillState.Completed));

    public static BackfillBatchResponse Summarize(LibraryBackfillBatch batch) => new(
        batch.Id, batch.KidProfileId, batch.Label, batch.State, batch.CreatedAt, batch.Error, batch.Items.Count,
        batch.Items.Count(item => item.State == BackfillItemState.Pending),
        batch.Items.Count(item => item.State == BackfillItemState.Applied),
        batch.Items.Count(item => item.State == BackfillItemState.Preserved),
        batch.Items.Count(item => item.State == BackfillItemState.Skipped),
        batch.Items.Count(item => item.State == BackfillItemState.Undone));

    public async Task<BackfillPreview> PreviewAsync(int profileId, int offset, CancellationToken ct)
    {
        if (offset < 0) throw new BackfillException(400, "The offset cannot be negative.");
        KidProfile profile = await this.ProfileAsync(profileId, ct);
        string serverId = await this.ReadyServerAsync(ct);
        IQueryable<LibraryItem> query = db.LibraryItems.AsNoTracking().Where(item => item.ServerId == serverId);
        int total = await query.CountAsync(ct);
        List<LibraryItem> items = await query.Include(item => item.CanonicalTitle).OrderBy(item => item.Id).Skip(offset).Take(PageSize).ToListAsync(ct);
        Dictionary<TitleKey, ContentDecision> decisions = [];
        List<BackfillPreviewItem> preview = [];
        ContentPolicy policy = profile.ContentPolicy();
        using CancellationTokenSource budget = new(RequestQueue.PageBudget, time);
        foreach (LibraryItem item in items)
        {
            int? tmdbId = item.CanonicalTitle?.TmdbId;
            bool selectable = tmdbId is > 0 && item.Guid.Length > 0 && item.CanonicalTitle!.Kind == item.Kind;
            ContentDecision decision = new(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.MetadataUnavailable));
            if (selectable && !budget.IsCancellationRequested)
            {
                TitleKey key = new(item.Kind, tmdbId!.Value);
                if (!decisions.TryGetValue(key, out ContentDecision? found))
                {
                    bool revoked = await db.MediaRequests.AnyAsync(request => request.KidProfileId == profileId && request.Kind == key.Kind && request.TmdbId == key.TmdbId && request.State == Core.Requests.RequestState.Revoked, ct);
                    using CancellationTokenSource timeout = new(RequestQueue.CallLimit, time);
                    using CancellationTokenSource lookup = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token, timeout.Token);
                    found = (await KidPolicyEvaluator.EvaluateAsync(tmdb, key, policy, revoked, lookup.Token, keepPartialOnCancel: true))?.Decision ?? decision;
                    ct.ThrowIfCancellationRequested();
                    decisions[key] = found;
                }

                decision = found;
                selectable = decision.Reason.Code != ContentReasonCode.MetadataUnavailable;
            }
            else
            {
                selectable = false;
            }

            preview.Add(new BackfillPreviewItem(item.Id, item.Guid, item.Title, item.Kind, tmdbId, decision.Outcome, decision.Reason, selectable));
        }

        return new BackfillPreview(profileId, serverId, profile.PlexLabel, preview, total, offset, PageSize);
    }

    public async Task<BackfillBatchResponse?> CreateAsync(BackfillApply input, CancellationToken ct)
    {
        if (input.Items is null || input.Items.Count is < 1 or > MaxSelection || input.Items.Any(item => item is null || item.LibraryItemId <= 0 || string.IsNullOrEmpty(item.Guid)) || input.Items.Select(item => item.LibraryItemId).Distinct().Count() != input.Items.Count)
            throw new BackfillException(400, "Select between 1 and 10000 different library items.");
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            KidProfile profile = await this.ProfileAsync(input.KidProfileId, ct);
            string serverId = await this.ReadyServerAsync(ct);
            if (serverId != input.ServerId || profile.PlexLabel != input.Label) throw new BackfillException(409, "The server or profile label changed. Preview the library again.");
            int[] ids = [.. input.Items.Select(item => item.LibraryItemId)];
            List<LibraryItem> items = await db.LibraryItems.AsNoTracking().Include(item => item.CanonicalTitle).Where(item => ids.Contains(item.Id) && item.ServerId == serverId).OrderBy(item => item.Id).ToListAsync(ct);
            Dictionary<int, string> expected = input.Items.ToDictionary(item => item.LibraryItemId, item => item.Guid);
            if (items.Count != ids.Length || items.Any(item => item.Guid != expected[item.Id] || item.CanonicalTitle is null || item.CanonicalTitle.TmdbId <= 0 || item.CanonicalTitle.Kind != item.Kind))
                throw new BackfillException(409, "A selected title changed or has no TMDB match. Preview the library again.");
            if (input.DryRun) return null;
            LibraryBackfillBatch batch = new()
            {
                KidProfileId = profile.Id,
                ServerId = serverId,
                Label = profile.PlexLabel,
                State = BackfillState.Applying,
                CreatedAt = time.GetUtcNow(),
                Items = [.. items.Select(item => new LibraryBackfillItem
                {
                    RatingKey = item.RatingKey,
                    Guid = item.Guid,
                    Title = item.Title,
                    Kind = item.Kind,
                    TmdbId = item.CanonicalTitle!.TmdbId,
                })],
            };
            db.LibraryBackfillBatches.Add(batch);
            await db.SaveChangesAsync(ct);
            return Summarize(batch);
        }
        finally { gate.Semaphore.Release(); }
    }

    public async Task<BackfillBatchResponse> ChangeAsync(int id, bool undo, CancellationToken ct)
    {
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            LibraryBackfillBatch batch = await db.LibraryBackfillBatches.Include(batch => batch.Items).SingleOrDefaultAsync(batch => batch.Id == id, ct)
                ?? throw new BackfillException(404, "The backfill batch does not exist.");
            if (undo)
            {
                if (batch.State == BackfillState.Undone) return Summarize(batch);
                batch.State = BackfillState.Undoing;
                foreach (LibraryBackfillItem item in batch.Items.Where(item => item.State == BackfillItemState.Pending && !item.OwnsLabel)) item.State = BackfillItemState.Preserved;
            }
            else if (batch.State == BackfillState.Paused)
            {
                batch.State = BackfillState.Applying;
            }
            else if (batch.State == BackfillState.UndoPaused)
            {
                batch.State = BackfillState.Undoing;
            }
            batch.Error = null;
            await db.SaveChangesAsync(ct);
            return Summarize(batch);
        }
        finally { gate.Semaphore.Release(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            List<LibraryBackfillBatch> batches = await db.LibraryBackfillBatches.Include(batch => batch.Items.OrderBy(item => item.Id))
                .Where(batch => batch.State == BackfillState.Applying || batch.State == BackfillState.Undoing).OrderBy(batch => batch.Id).ToListAsync(ct);
            int remaining = ChunkSize;
            foreach (LibraryBackfillBatch batch in batches)
            {
                if (remaining == 0) break;
                bool undo = batch.State == BackfillState.Undoing;
                try
                {
                    if (await scanner.GetServerIdAsync(ct) != batch.ServerId) throw new BackfillException(409, "Select the batch's original Plex server to continue.");
                    if (!undo && !await db.KidProfiles.AnyAsync(profile => profile.Id == batch.KidProfileId && profile.ArchivedAt == null && profile.PlexLabel == batch.Label, ct))
                        throw ProfileChanged();
                    string source = Source(batch.Id);
                    string undoSource = UndoSource(batch.Id);
                    if (undo)
                    {
                        using IDisposable held = await profileLabels.EnterAsync(ct);
                        await this.CheckUndoLabelAsync(batch, source, undoSource, ct);
                        // The durable marker stops the guardian restoring an earlier copy while the remaining copies are undone in later chunks.
                        await db.OwnedLabels.Where(row => row.Source == source && row.ServerId == batch.ServerId && row.KidProfileId == batch.KidProfileId && row.Label == batch.Label)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Source, undoSource), ct);
                    }

                    IEnumerable<LibraryBackfillItem> pending = batch.Items.Where(item => undo
                        ? item.State is BackfillItemState.Pending or BackfillItemState.Applied
                        : item.State == BackfillItemState.Pending);
                    foreach (LibraryBackfillItem item in pending.Take(remaining))
                    {
                        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        deadline.CancelAfter(options.Value.LabelTimeout);
                        LabelWriteResult written = undo
                            ? await labels.RemoveBackfillAsync(item.RatingKey, batch.Label, batch.KidProfileId, undoSource, batch.ServerId, item.Guid, deadline.Token)
                            : await labels.AddBackfillAsync(item.RatingKey, batch.Label, batch.KidProfileId, source, batch.ServerId, item.Guid, () => item.OwnsLabel = true, deadline.Token);
                        if (written.Outcome == LabelWriteOutcome.LabelInUse)
                        {
                            throw LabelReused();
                        }
                        if (written.Outcome == LabelWriteOutcome.ProfileUnavailable)
                        {
                            throw ProfileChanged();
                        }
                        if (written.Outcome == LabelWriteOutcome.ItemNotFound)
                        {
                            item.State = BackfillItemState.Skipped;
                            item.Detail = "The Plex item disappeared or its identity changed. No label was changed.";
                        }
                        else if (undo)
                        {
                            item.State = BackfillItemState.Undone;
                            item.Detail = written.Outcome == LabelWriteOutcome.NotOwned ? "The label belongs outside this batch and was preserved." : null;
                        }
                        else
                        {
                            bool owned = await db.OwnedLabels.AsNoTracking().AnyAsync(row => row.ServerId == batch.ServerId && row.Guid == item.Guid && row.Label == batch.Label && row.Source == source, ct);
                            item.State = owned && item.OwnsLabel ? BackfillItemState.Applied : BackfillItemState.Preserved;
                            item.Detail = owned && item.OwnsLabel ? null : "An existing label or ownership was preserved.";
                        }

                        await db.SaveChangesAsync(CancellationToken.None);
                        remaining--;
                    }

                    if (!batch.Items.Any(item => item.State == BackfillItemState.Pending || (undo && item.State == BackfillItemState.Applied)))
                    {
                        if (undo)
                        {
                            using IDisposable held = await profileLabels.EnterAsync(ct);
                            await this.CheckUndoLabelAsync(batch, source, undoSource, ct);
                            // Keep ownership for skipped identities: a parent can inspect those labels rather than losing their audit trail.
                            string[] skipped = [.. batch.Items.Where(item => item.State == BackfillItemState.Skipped).Select(item => item.Guid)];
                            await db.OwnedLabels.Where(row => row.Source == undoSource && !skipped.Contains(row.Guid)).ExecuteDeleteAsync(ct);
                        }

                        batch.State = undo ? BackfillState.Undone : BackfillState.Completed;
                        await db.SaveChangesAsync(CancellationToken.None);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is BackfillException or MediaServerException or HttpRequestException or OperationCanceledException)
                {
                    batch.State = undo ? BackfillState.UndoPaused : BackfillState.Paused;
                    batch.Error = ex is BackfillException ? ex.Message : "Plex did not finish the label change. Resume to retry; a timed-out change may already have reached Plex.";
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
        }
        finally { gate.Semaphore.Release(); }
    }

    private async Task<KidProfile> ProfileAsync(int id, CancellationToken ct) =>
        await db.KidProfiles.AsNoTracking().FirstWithContentRulesAsync(profile => profile.Id == id && profile.ArchivedAt == null, ct)
        ?? throw new BackfillException(404, "The active kid profile does not exist.");

    private static BackfillException LabelReused() => new(409, "Another active profile uses this label, so undo is paused and its labels and ledger were preserved. Change that profile's label before resuming undo.");

    private static BackfillException ProfileChanged() => new(409, "The profile was archived or its label changed. Undo this batch or restore the profile before resuming.");

    private async Task CheckUndoLabelAsync(LibraryBackfillBatch batch, string source, string undoSource, CancellationToken ct)
    {
        if (await labels.LabelInUseAsync(batch.KidProfileId, batch.Label, ct)
            && await db.OwnedLabels.AnyAsync(row => row.ServerId == batch.ServerId && row.KidProfileId == batch.KidProfileId && row.Label == batch.Label
                && (row.Source == source || row.Source == undoSource), ct))
        {
            throw LabelReused();
        }
    }

    private async Task<string> ReadyServerAsync(CancellationToken ct)
    {
        LibraryRefreshState? state = await db.LibraryRefreshStates.AsNoTracking().SingleOrDefaultAsync(ct);
        string? serverId = await scanner.GetServerIdAsync(ct);
        return state is { LastFullRefreshAt: not null, ServerId: not null } && state.ServerId == serverId
            ? serverId
            : throw new BackfillException(409, "Complete a full library refresh for the selected Plex server before backfilling.");
    }
}

public sealed class LibraryBackfillJob(LibraryBackfill backfill) : IBackgroundJob
{
    public const string JobName = "library-backfill";

    public Task RunAsync(CancellationToken ct) => backfill.RunAsync(ct);
}
