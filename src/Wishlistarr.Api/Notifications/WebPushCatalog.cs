using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public sealed record WebPushInput(string? Endpoint, string? P256dh, string? Auth);

public sealed record WebPushSave(int Status, int Id, string? Error);

public sealed record WebPushSubscriptionSummary(int Id, DateTimeOffset CreatedAt);

public sealed class WebPushCatalog(WishlistarrDbContext db, VapidKeys keys, TimeProvider time)
{
    public const string InvalidSubscription = "This browser subscription is not valid.";
    public const string HttpsRequired = "Use an https push endpoint.";
    public const string AlreadySubscribed = "That push endpoint is already subscribed.";
    public const string NotFound = "That subscription was not found.";

    public Task<string> PublicKeyAsync(CancellationToken ct) => keys.PublicKeyAsync(ct);

    public async Task<IReadOnlyList<WebPushSubscriptionSummary>> ListParentAsync(int parentId, CancellationToken ct) =>
        await db.WebPushSubscriptions.AsNoTracking()
            .Where(row => row.ParentAccountId == parentId)
            .OrderBy(row => row.Id)
            .Select(row => new WebPushSubscriptionSummary(row.Id, row.CreatedAt))
            .ToListAsync(ct);

    public async Task<WebPushSave> SaveParentAsync(int parentId, WebPushInput? input, CancellationToken ct)
    {
        if (!TryRead(input, out string endpoint, out string p256dh, out string auth, out string error))
        {
            return new WebPushSave(StatusCodes.Status400BadRequest, 0, error);
        }

        WebPushSubscription? existing = await db.WebPushSubscriptions.FirstOrDefaultAsync(row => row.Endpoint == endpoint, ct);
        if (existing is not null && existing.ParentAccountId != parentId)
        {
            return new WebPushSave(StatusCodes.Status409Conflict, 0, AlreadySubscribed);
        }

        if (existing is not null)
        {
            existing.P256dh = p256dh;
            existing.Auth = auth;
            await db.SaveChangesAsync(ct);
            return new WebPushSave(StatusCodes.Status200OK, existing.Id, null);
        }

        WebPushSubscription created = new()
        {
            ParentAccountId = parentId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedAt = time.GetUtcNow(),
        };
        db.WebPushSubscriptions.Add(created);
        await db.SaveChangesAsync(ct);
        return new WebPushSave(StatusCodes.Status201Created, created.Id, null);
    }

    public async Task<bool> DeleteParentAsync(int parentId, int id, CancellationToken ct)
    {
        WebPushSubscription? subscription = await db.WebPushSubscriptions
            .FirstOrDefaultAsync(row => row.Id == id && row.ParentAccountId == parentId, ct);
        if (subscription is null)
        {
            return false;
        }

        db.WebPushSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<bool> DeviceSubscribedAsync(int deviceId, CancellationToken ct) =>
        db.WebPushSubscriptions.AnyAsync(row => row.KidDeviceId == deviceId, ct);

    public async Task<WebPushSave> SaveDeviceAsync(int deviceId, WebPushInput? input, CancellationToken ct)
    {
        if (!TryRead(input, out string endpoint, out string p256dh, out string auth, out string error))
        {
            return new WebPushSave(StatusCodes.Status400BadRequest, 0, error);
        }

        WebPushSubscription? current = await db.WebPushSubscriptions.FirstOrDefaultAsync(row => row.KidDeviceId == deviceId, ct);
        WebPushSubscription? owner = await db.WebPushSubscriptions.FirstOrDefaultAsync(row => row.Endpoint == endpoint, ct);
        if (owner is not null && owner.Id != current?.Id)
        {
            return new WebPushSave(StatusCodes.Status409Conflict, 0, AlreadySubscribed);
        }

        if (current is not null && current.Endpoint == endpoint)
        {
            current.P256dh = p256dh;
            current.Auth = auth;
            await db.SaveChangesAsync(ct);
            return new WebPushSave(StatusCodes.Status200OK, current.Id, null);
        }

        if (current is not null)
        {
            db.WebPushSubscriptions.Remove(current);
        }

        WebPushSubscription created = new()
        {
            KidDeviceId = deviceId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedAt = time.GetUtcNow(),
        };
        db.WebPushSubscriptions.Add(created);
        await db.SaveChangesAsync(ct);
        return new WebPushSave(StatusCodes.Status201Created, created.Id, null);
    }

    public async Task DeleteDeviceAsync(int deviceId, CancellationToken ct)
    {
        List<int> ids = await db.WebPushSubscriptions.Where(row => row.KidDeviceId == deviceId).Select(row => row.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return;
        }

        await db.WebPushDeliveries.Where(row => ids.Contains(row.WebPushSubscriptionId)).ExecuteDeleteAsync(ct);
        await db.WebPushSubscriptions.Where(row => ids.Contains(row.Id)).ExecuteDeleteAsync(ct);
    }

    private static bool TryRead(WebPushInput? input, out string endpoint, out string p256dh, out string auth, out string error)
    {
        endpoint = string.Empty;
        p256dh = string.Empty;
        auth = string.Empty;
        error = InvalidSubscription;
        if (input?.Endpoint is not string raw || input.P256dh is not string rawKey || input.Auth is not string rawAuth)
        {
            return false;
        }

        raw = raw.Trim();
        if (raw.Length is 0 or > 2000 || raw.Any(char.IsControl) || !Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            error = HttpsRequired;
            return false;
        }

        if (!TryKey(rawKey, WebPushEncryption.PublicKeyLength, out string key) || !TryKey(rawAuth, WebPushEncryption.AuthLength, out string secret))
        {
            return false;
        }

        if (!WebPushEncryption.TryPublic(Base64Url.DecodeFromChars(key), out _))
        {
            return false;
        }

        endpoint = raw;
        p256dh = key;
        auth = secret;
        error = string.Empty;
        return true;
    }

    private static bool TryKey(string raw, int length, out string encoded)
    {
        encoded = string.Empty;
        try
        {
            byte[] bytes = Base64Url.DecodeFromChars(raw.Trim());
            if (bytes.Length != length)
            {
                return false;
            }

            encoded = Base64Url.EncodeToString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
