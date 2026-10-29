using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Kids;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Labels;

public enum LabelWriteOutcome
{
    Added,
    AlreadyPresent,
    Removed,
    NotPresent,
    NotOwned,
    ItemNotFound,
    LabelInUse,
    ProfileUnavailable,
}

public sealed record LabelWriteResult(LabelWriteOutcome Outcome, MediaItemLabels? Item = null);

public sealed record ProfileLabels(MediaItemLabels Item, IReadOnlyList<OwnedLabel> Owned);

public sealed class LabelWriter(WishlistarrDbContext db, IMediaServer server, ItemLocks locks, TimeProvider time, IProfileLabelGate profileLabels)
{
    public const int MaxAttempts = 3;
    public const int MaxSourceLength = 32;

    public static StringComparer LabelComparer { get; } = StringComparer.OrdinalIgnoreCase;

    public async Task<LabelWriteResult> AddBackfillAsync(string itemId, string label, int profileId, string source, string serverId, string guid, Action reserveItem, CancellationToken ct)
    {
        using IDisposable held = await profileLabels.EnterAsync(ct);
        if (!await db.KidProfiles.AnyAsync(profile => profile.Id == profileId && profile.ArchivedAt == null && profile.PlexLabel == label, ct))
            return new LabelWriteResult(LabelWriteOutcome.ProfileUnavailable);
        return await this.WriteAsync(
            itemId,
            async (item, token) =>
            {
                OwnedLabel? owned = await this.FindOwnedAsync(item, label, token);
                if (item.Labels.Contains(label, LabelComparer) || (owned is not null && owned.Source != source)) return null;
                return [.. item.Labels, label];
            },
            item => item.Labels.Contains(label, LabelComparer),
            item => this.ReserveAsync(item, label, profileId, source, reserveItem),
            (_, _) => Task.CompletedTask,
            LabelWriteOutcome.Added,
            LabelWriteOutcome.AlreadyPresent,
            ct,
            serverId,
            guid,
            keepReservationOnFailure: true);
    }

    public async Task<bool> LabelInUseAsync(int profileId, string label, CancellationToken ct)
    {
        List<string> active = await db.KidProfiles.AsNoTracking().Where(profile => profile.Id != profileId && profile.ArchivedAt == null)
            .Select(profile => profile.PlexLabel).ToListAsync(ct);
        return active.Contains(label, LabelComparer);
    }

    public async Task<LabelWriteResult> RemoveBackfillAsync(string itemId, string label, int profileId, string source, string serverId, string guid, CancellationToken ct)
    {
        using IDisposable held = await profileLabels.EnterAsync(ct);
        bool owned = false;
        bool inUse = false;
        LabelWriteResult result = await this.WriteAsync(
            itemId,
            async (item, token) =>
            {
                owned = (await this.FindOwnedAsync(item, label, token))?.Source == source;
                // A profile can reuse an archived or renamed profile's label while an earlier undo is still pending.
                inUse = owned && await this.LabelInUseAsync(profileId, label, token);
                if (inUse) return null;
                return owned && item.Labels.Contains(label, LabelComparer)
                    ? [.. item.Labels.Where(existing => !LabelComparer.Equals(existing, label))]
                    : null;
            },
            item => !item.Labels.Contains(label, LabelComparer),
            _ => Task.FromResult<Func<Task>>(() => Task.CompletedTask),
            (_, _) => Task.CompletedTask,
            LabelWriteOutcome.Removed,
            LabelWriteOutcome.NotPresent,
            ct,
            serverId,
            guid);
        return inUse ? result with { Outcome = LabelWriteOutcome.LabelInUse }
            : result.Outcome == LabelWriteOutcome.NotPresent && !owned ? result with { Outcome = LabelWriteOutcome.NotOwned } : result;
    }

    public async Task<LabelWriteResult> AddAsync(string itemId, string label, int kidProfileId, string source, CancellationToken ct = default)
    {
        if (PlexLabelRules.Validate(label) is string error)
        {
            throw new ArgumentException(error, nameof(label));
        }

        if (source.Length is 0 or > MaxSourceLength)
        {
            throw new ArgumentException($"Sources are 1 to {MaxSourceLength} characters.", nameof(source));
        }

        if (!await db.KidProfiles.AnyAsync(profile => profile.Id == kidProfileId, ct))
        {
            throw new ArgumentException("The kid profile does not exist.", nameof(kidProfileId));
        }

        return await this.WriteAsync(
            itemId,
            async (item, token) =>
            {
                if (item.Labels.Contains(label, LabelComparer))
                {
                    OwnedLabel? existing = await this.FindOwnedAsync(item, label, token);
                    if (existing is not null && (existing.Source.StartsWith("backfill:", StringComparison.Ordinal) || existing.Source.StartsWith("undo:", StringComparison.Ordinal)))
                    {
                        // A later explicit grant must survive undoing the earlier bulk operation, including while that undo is paused.
                        existing.Source = source;
                        existing.KidProfileId = kidProfileId;
                        existing.AppliedAt = time.GetUtcNow();
                        await db.SaveChangesAsync(token);
                    }

                    return null;
                }

                OwnedLabel? owned = await this.FindOwnedAsync(item, label, token);
                return [.. item.Labels, owned?.Label ?? label];
            },
            item => item.Labels.Contains(label, LabelComparer),
            item => this.ReserveAsync(item, label, kidProfileId, source),
            (_, _) => Task.CompletedTask,
            LabelWriteOutcome.Added,
            LabelWriteOutcome.AlreadyPresent,
            ct);
    }

    public async Task<LabelWriteResult> RemoveAsync(string itemId, string label, CancellationToken ct = default)
    {
        bool owned = false;
        LabelWriteResult result = await this.WriteAsync(
            itemId,
            async (item, token) =>
            {
                owned = await this.FindOwnedAsync(item, label, token) is not null;
                return owned && item.Labels.Contains(label, LabelComparer)
                    ? [.. item.Labels.Where(existing => !LabelComparer.Equals(existing, label))]
                    : null;
            },
            item => !item.Labels.Contains(label, LabelComparer),
            _ => Task.FromResult<Func<Task>>(() => Task.CompletedTask),
            (item, token) => this.ForgetAsync(item, label, token),
            LabelWriteOutcome.Removed,
            LabelWriteOutcome.NotPresent,
            ct);

        if (result.Outcome == LabelWriteOutcome.NotPresent)
        {
            if (!owned)
            {
                return result with { Outcome = LabelWriteOutcome.NotOwned };
            }

            await this.ForgetAsync(result.Item!, label, ct);
        }

        return result;
    }

    // Puts back a label the ledger already owns on the item's guid. It never creates a ledger row, so a label that was revoked in the meantime stays off,
    // and the owning profile is checked at the moment of writing, so a profile archived in the meantime does not get its label back.
    public async Task<LabelWriteResult> RestoreAsync(string itemId, string label, CancellationToken ct = default)
    {
        bool owned = true;
        LabelWriteResult result = await this.WriteAsync(
            itemId,
            async (item, token) =>
            {
                OwnedLabel? row = await this.FindOwnedAsync(item, label, token);
                owned = row is not null
                    && !row.Source.StartsWith("undo:", StringComparison.Ordinal)
                    && await db.KidProfiles.AnyAsync(profile => profile.Id == row.KidProfileId && profile.ArchivedAt == null, token);
                if (owned && row!.Source.StartsWith("backfill:", StringComparison.Ordinal))
                {
                    // Recheck the copy claim at the write boundary, because a guardian plan can predate a batch's apply or undo transition.
                    owned = LibraryBackfill.TryBatchId(row.Source, out int batchId)
                        && await LibraryBackfill.RepairableCopies(db, batchId).AnyAsync(copy => copy.RatingKey == item.Id && copy.Guid == item.Guid
                            && copy.Batch!.ServerId == item.ServerId && copy.Batch.KidProfileId == row.KidProfileId, token);
                }

                return !owned || item.Labels.Contains(label, LabelComparer)
                    ? null
                    : [.. item.Labels, row!.Label];
            },
            item => item.Labels.Contains(label, LabelComparer),
            item => this.PointAtAsync(item, label),
            (_, _) => Task.CompletedTask,
            LabelWriteOutcome.Added,
            LabelWriteOutcome.AlreadyPresent,
            ct);

        return result.Outcome == LabelWriteOutcome.AlreadyPresent && !owned
            ? result with { Outcome = LabelWriteOutcome.NotOwned }
            : result;
    }

    public async Task<ProfileLabels?> FindProfileLabelsAsync(string itemId, int kidProfileId, CancellationToken ct = default)
    {
        MediaItemLabels? item = await server.GetLabelsAsync(itemId, ct);
        if (item is null)
        {
            return null;
        }

        List<OwnedLabel> owned = await db.OwnedLabels.AsNoTracking()
            .Where(OwnedOn(item))
            .Where(row => row.KidProfileId == kidProfileId)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        return new ProfileLabels(item, owned);
    }

    public async Task<bool> OwnsAsync(MediaItemLabels item, string label, CancellationToken ct = default) =>
        await this.FindOwnedAsync(item, label, ct) is not null;

    private static Expression<Func<OwnedLabel, bool>> OwnedOn(MediaItemLabels item)
    {
        string serverId = item.ServerId;
        string guid = item.Guid;
        return owned => owned.ServerId == serverId && owned.Guid == guid;
    }

    private async Task<LabelWriteResult> WriteAsync(
        string itemId,
        Func<MediaItemLabels, CancellationToken, Task<IReadOnlyList<string>?>> plan,
        Func<MediaItemLabels, bool> verify,
        Func<MediaItemLabels, Task<Func<Task>>> reserve,
        Func<MediaItemLabels, CancellationToken, Task> record,
        LabelWriteOutcome changed,
        LabelWriteOutcome unchanged,
        CancellationToken ct,
        string? expectedServerId = null,
        string? expectedGuid = null,
        bool keepReservationOnFailure = false)
    {
        MediaItemLabels? target = await server.GetLabelsAsync(itemId, ct);
        if (target is null || (expectedServerId is not null && (target.ServerId != expectedServerId || target.Guid != expectedGuid)))
        {
            return new LabelWriteResult(LabelWriteOutcome.ItemNotFound);
        }

        if (string.IsNullOrEmpty(target.Guid))
        {
            throw new MediaServerException("The Plex item has no guid, so its labels cannot be tracked.");
        }

        // Copies with the same guid share one ledger row, so their ownership checks and reservations must also share a lock.
        using IDisposable held = await locks.AcquireAsync($"{target.ServerId}/{target.Guid}", ct);
        Func<Task>? release = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            MediaItemLabels? current = await server.GetLabelsAsync(target.Id, ct);
            if (current is null || current.ServerId != target.ServerId || current.Guid != target.Guid)
            {
                return new LabelWriteResult(LabelWriteOutcome.ItemNotFound);
            }

            if (await plan(current, ct) is not IReadOnlyList<string> labels)
            {
                return new LabelWriteResult(unchanged, current);
            }

            // Plex has no conditional write, so a second read narrows the window in which an edit made elsewhere could be overwritten.
            MediaItemLabels? confirm = await server.GetLabelsAsync(target.Id, ct);
            if (confirm is null || confirm.ServerId != target.ServerId || confirm.Guid != target.Guid || !confirm.Labels.ToHashSet(LabelComparer).SetEquals(current.Labels))
            {
                continue;
            }

            // The ledger row is saved before Plex is touched, so a deadline that fires after Plex took the change cannot leave a label with no owner.
            release ??= await reserve(current);
            try
            {
                await server.SetLabelsAsync(current, labels, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!keepReservationOnFailure) await release();
                throw;
            }

            MediaItemLabels? written = await server.GetLabelsAsync(target.Id, ct);
            if (written is not null && written.ServerId == target.ServerId && written.Guid == target.Guid && written.Locked && verify(written))
            {
                await record(written, ct);
                return new LabelWriteResult(changed, written);
            }
        }

        if (release is not null && !keepReservationOnFailure)
        {
            await release();
        }

        throw new MediaServerException("Plex did not keep the label change.");
    }

    private async Task<OwnedLabel?> FindOwnedAsync(MediaItemLabels item, string label, CancellationToken ct)
    {
        OwnedLabel? owned = await db.OwnedLabels.Where(OwnedOn(item)).FirstOrDefaultAsync(owned => owned.Label == label, ct);
        if (owned is null) return null;
        // A batched caller can outlive a grant made in another scope; reload instead of trusting the context's tracked copy.
        await db.Entry(owned).ReloadAsync(ct);
        return db.Entry(owned).State == EntityState.Detached ? null : owned;
    }

    private async Task<Func<Task>> ReserveAsync(MediaItemLabels item, string label, int kidProfileId, string source, Action? reserveItem = null)
    {
        OwnedLabel? owned = await this.FindOwnedAsync(item, label, CancellationToken.None);
        bool created = owned is null;
        if (owned is null)
        {
            owned = new OwnedLabel
            {
                ServerId = item.ServerId,
                Guid = item.Guid,
                RatingKey = item.Id,
                Label = label,
                Source = source,
            };
            db.OwnedLabels.Add(owned);
        }

        owned.RatingKey = item.Id;
        owned.KidProfileId = kidProfileId;
        owned.Source = source;
        owned.AppliedAt = time.GetUtcNow();
        reserveItem?.Invoke();
        await db.SaveChangesAsync(CancellationToken.None);
        return async () =>
        {
            if (created)
            {
                db.OwnedLabels.Remove(owned);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        };
    }

    private async Task<Func<Task>> PointAtAsync(MediaItemLabels item, string label)
    {
        string serverId = item.ServerId;
        string guid = item.Guid;
        await db.OwnedLabels
            .Where(owned => owned.ServerId == serverId && owned.Guid == guid && owned.Label == label)
            .ExecuteUpdateAsync(setters => setters.SetProperty(owned => owned.RatingKey, item.Id), CancellationToken.None);
        return () => Task.CompletedTask;
    }

    private async Task ForgetAsync(MediaItemLabels item, string label, CancellationToken ct)
    {
        if (await this.FindOwnedAsync(item, label, ct) is OwnedLabel owned)
        {
            db.OwnedLabels.Remove(owned);
            await db.SaveChangesAsync(ct);
        }
    }
}
