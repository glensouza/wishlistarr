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
}

public sealed record LabelWriteResult(LabelWriteOutcome Outcome, MediaItemLabels? Item = null);

public sealed record ProfileLabels(MediaItemLabels Item, IReadOnlyList<OwnedLabel> Owned);

public sealed class LabelWriter(WishlistarrDbContext db, IMediaServer server, ItemLocks locks, TimeProvider time)
{
    public const int MaxAttempts = 3;
    public const int MaxSourceLength = 32;

    private static readonly StringComparer LabelComparer = StringComparer.OrdinalIgnoreCase;

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
                    return null;
                }

                OwnedLabel? owned = await this.FindOwnedAsync(item, label, token);
                return [.. item.Labels, owned?.Label ?? label];
            },
            item => item.Labels.Contains(label, LabelComparer),
            (item, token) => this.RecordAsync(item, label, kidProfileId, source, token),
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
        Func<MediaItemLabels, CancellationToken, Task> record,
        LabelWriteOutcome changed,
        LabelWriteOutcome unchanged,
        CancellationToken ct)
    {
        MediaItemLabels? target = await server.GetLabelsAsync(itemId, ct);
        if (target is null)
        {
            return new LabelWriteResult(LabelWriteOutcome.ItemNotFound);
        }

        if (string.IsNullOrEmpty(target.Guid))
        {
            throw new MediaServerException("The Plex item has no guid, so its labels cannot be tracked.");
        }

        using IDisposable held = await locks.AcquireAsync($"{target.ServerId}/{target.Id}", ct);
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            MediaItemLabels? current = await server.GetLabelsAsync(target.Id, ct);
            if (current is null)
            {
                return new LabelWriteResult(LabelWriteOutcome.ItemNotFound);
            }

            if (await plan(current, ct) is not IReadOnlyList<string> labels)
            {
                return new LabelWriteResult(unchanged, current);
            }

            // Plex has no conditional write, so a second read narrows the window in which an edit made elsewhere could be overwritten.
            MediaItemLabels? confirm = await server.GetLabelsAsync(target.Id, ct);
            if (confirm is null || !confirm.Labels.ToHashSet(LabelComparer).SetEquals(current.Labels))
            {
                continue;
            }

            await server.SetLabelsAsync(current, labels, ct);
            MediaItemLabels? written = await server.GetLabelsAsync(target.Id, ct);
            if (written is not null && written.Locked && verify(written))
            {
                await record(written, ct);
                return new LabelWriteResult(changed, written);
            }
        }

        throw new MediaServerException("Plex did not keep the label change.");
    }

    private Task<OwnedLabel?> FindOwnedAsync(MediaItemLabels item, string label, CancellationToken ct) =>
        db.OwnedLabels.Where(OwnedOn(item)).FirstOrDefaultAsync(owned => owned.Label == label, ct);

    private async Task RecordAsync(MediaItemLabels item, string label, int kidProfileId, string source, CancellationToken ct)
    {
        OwnedLabel? owned = await this.FindOwnedAsync(item, label, ct);
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
        await db.SaveChangesAsync(ct);
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
