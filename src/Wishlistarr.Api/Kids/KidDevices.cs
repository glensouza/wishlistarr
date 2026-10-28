using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Security;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Kids;

public sealed class KidDevices(WishlistarrDbContext db, IOptions<KidOptions> options, TimeProvider time)
{
    public const string CookieName = "wishlistarr.device";
    public const int MaxNameLength = 50;
    public const int CodeLength = 8;

    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int TokenBytes = 32;

    private static readonly object CurrentDeviceKey = new();
    private static readonly byte[] NoMatch = new byte[SHA256.HashSizeInBytes];

    public async Task<KidDevice?> FindCurrentAsync(HttpContext context, CancellationToken ct = default)
    {
        if (context.Items.TryGetValue(CurrentDeviceKey, out object? cached))
        {
            return cached as KidDevice;
        }

        KidDevice? device = await this.FindByTokenAsync(context.Request.Cookies[CookieName], ct);
        context.Items[CurrentDeviceKey] = device;
        return device;
    }

    public async Task<IReadOnlyList<KidProfile>> ListProfilesAsync(int deviceId, CancellationToken ct = default) =>
        await db.KidDeviceProfiles
            .AsNoTracking()
            .Where(assignment => assignment.KidDeviceId == deviceId && assignment.KidProfile!.ArchivedAt == null)
            .Select(assignment => assignment.KidProfile!)
            .OrderBy(profile => profile.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<KidDeviceSummary>> ListAsync(int? currentDeviceId, CancellationToken ct = default)
    {
        Dictionary<int, DateTimeOffset> codes = await this.ActiveCodesAsync(ct);
        List<KidDevice> devices = await db.KidDevices
            .AsNoTracking()
            .Include(device => device.Profiles)
            .OrderBy(device => device.Id)
            .ToListAsync(ct);
        return [.. devices.Select(device => KidDeviceSummary.From(
            device,
            codes.TryGetValue(device.Id, out DateTimeOffset expiresAt) ? expiresAt : null,
            device.Id == currentDeviceId))];
    }

    public async Task<DeviceOutcome> CreateAsync(KidDeviceRequest request, CancellationToken ct = default)
    {
        if (await this.ValidateAsync(request, ct) is not (string name, int[] profileIds))
        {
            return DeviceOutcome.Invalid(InvalidMessage(request));
        }

        KidDevice device = new() { Name = name, CreatedAt = time.GetUtcNow() };
        foreach (int profileId in profileIds)
        {
            device.Profiles.Add(new KidDeviceProfile { KidProfileId = profileId });
        }

        db.KidDevices.Add(device);
        await db.SaveChangesAsync(ct);
        PairingCode code = await this.IssueCodeAsync(device.Id, ct) ?? throw new InvalidOperationException("The new device was not saved.");
        return DeviceOutcome.Ok(KidDeviceSummary.From(device, code.ExpiresAt, isCurrent: false), code);
    }

    public async Task<DeviceOutcome> UpdateAsync(int id, KidDeviceRequest request, int? currentDeviceId, CancellationToken ct = default)
    {
        if (await this.ValidateAsync(request, ct) is not (string name, int[] profileIds))
        {
            return DeviceOutcome.Invalid(InvalidMessage(request));
        }

        KidDevice? device = await db.KidDevices.Include(row => row.Profiles).FirstOrDefaultAsync(row => row.Id == id, ct);
        if (device is null)
        {
            return DeviceOutcome.NotFound();
        }

        device.Name = name;
        int[] removed = [.. device.Profiles.Select(assignment => assignment.KidProfileId).Except(profileIds)];
        device.Profiles.RemoveAll(assignment => removed.Contains(assignment.KidProfileId));
        foreach (int profileId in profileIds.Except(device.Profiles.Select(assignment => assignment.KidProfileId)).ToList())
        {
            device.Profiles.Add(new KidDeviceProfile { KidProfileId = profileId });
        }

        await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            await db.KidSessions
                .Where(session => session.KidDeviceId == id && removed.Contains(session.KidProfileId))
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }

        Dictionary<int, DateTimeOffset> codes = await this.ActiveCodesAsync(ct);
        return DeviceOutcome.Ok(KidDeviceSummary.From(
            device,
            codes.TryGetValue(device.Id, out DateTimeOffset expiresAt) ? expiresAt : null,
            device.Id == currentDeviceId));
    }

    public async Task<PairingCode?> IssueCodeAsync(int deviceId, CancellationToken ct = default)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.KidDevices.AnyAsync(device => device.Id == deviceId, ct))
        {
            return null;
        }

        DateTimeOffset now = time.GetUtcNow();
        int[] stale = [.. (await db.KidPairingCodes.AsNoTracking().ToListAsync(ct))
            .Where(code => code.KidDeviceId == deviceId || code.ExpiresAt <= now)
            .Select(code => code.Id)];
        await db.KidPairingCodes.Where(code => stale.Contains(code.Id)).ExecuteDeleteAsync(ct);

        string code = string.Create(CodeLength, 0, static (span, _) =>
        {
            for (int index = 0; index < span.Length; index++)
            {
                span[index] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            }
        });
        KidPairingCode stored = new()
        {
            KidDeviceId = deviceId,
            CodeHash = HashCode(code),
            CreatedAt = now,
            ExpiresAt = now + options.Value.PairingCodeLifetime,
        };
        db.KidPairingCodes.Add(stored);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new PairingCode(code[..(CodeLength / 2)] + "-" + code[(CodeLength / 2)..], stored.ExpiresAt);
    }

    public async Task<KidDevice?> RedeemAsync(HttpContext context, string? code, CancellationToken ct = default)
    {
        string? normalized = NormalizeCode(code);
        byte[] presented = normalized is null ? NoMatch : Convert.FromHexString(HashCode(normalized));
        DateTimeOffset now = time.GetUtcNow();

        KidPairingCode? match = null;
        foreach (KidPairingCode candidate in await db.KidPairingCodes.AsNoTracking().ToListAsync(ct))
        {
            if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(candidate.CodeHash), presented))
            {
                match = candidate;
            }
        }

        if (normalized is null || match is null || match.ExpiresAt <= now)
        {
            return null;
        }

        int codeId = match.Id;
        int deviceId = match.KidDeviceId;
        byte[] secret = RandomNumberGenerator.GetBytes(TokenBytes);
        string tokenHash = Convert.ToHexString(SHA256.HashData(secret));
        await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Deleting the code is the claim: of several concurrent redemptions only one delete can affect the row.
            if (await db.KidPairingCodes.Where(row => row.Id == codeId).ExecuteDeleteAsync(ct) != 1)
            {
                return null;
            }

            await db.KidSessions.Where(session => session.KidDeviceId == deviceId).ExecuteDeleteAsync(ct);
            int updated = await db.KidDevices
                .Where(device => device.Id == deviceId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(device => device.TokenHash, tokenHash)
                        .SetProperty(device => device.PairedAt, now)
                        .SetProperty(device => device.LastSeenAt, now),
                    ct);
            if (updated != 1)
            {
                return null;
            }

            await this.DeletePushAsync(deviceId, ct);
            await transaction.CommitAsync(ct);
        }

        KidDevice paired = await db.KidDevices.AsNoTracking().FirstAsync(device => device.Id == deviceId, ct);
        this.WriteCookie(context, paired.Id.ToString(CultureInfo.InvariantCulture) + "." + Base64Url.EncodeToString(secret));
        context.Items[CurrentDeviceKey] = paired;
        return paired;
    }

    public void RefreshCookie(HttpContext context)
    {
        if (context.Request.Cookies[CookieName] is string value)
        {
            this.WriteCookie(context, value);
        }
    }

    public async Task<bool> RevokeAsync(int id, CancellationToken ct = default)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        await db.KidSessions.Where(session => session.KidDeviceId == id).ExecuteDeleteAsync(ct);
        await db.KidPinAttempts.Where(attempt => attempt.KidDeviceId == id).ExecuteDeleteAsync(ct);
        await db.KidPairingCodes.Where(code => code.KidDeviceId == id).ExecuteDeleteAsync(ct);
        await db.KidDeviceProfiles.Where(assignment => assignment.KidDeviceId == id).ExecuteDeleteAsync(ct);
        await this.DeletePushAsync(id, ct);
        bool removed = await db.KidDevices.Where(device => device.Id == id).ExecuteDeleteAsync(ct) > 0;
        await transaction.CommitAsync(ct);
        return removed;
    }

    public static string? NormalizeCode(string? code)
    {
        if (code is null || code.Length > CodeLength * 2)
        {
            return null;
        }

        StringBuilder builder = new(CodeLength);
        foreach (char character in code)
        {
            if (character is '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            char upper = char.ToUpperInvariant(character) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                char other => other,
            };
            if (!CodeAlphabet.Contains(upper, StringComparison.Ordinal))
            {
                return null;
            }

            builder.Append(upper);
        }

        return builder.Length == CodeLength ? builder.ToString() : null;
    }

    private async Task DeletePushAsync(int deviceId, CancellationToken ct)
    {
        List<int> ids = await db.WebPushSubscriptions.Where(row => row.KidDeviceId == deviceId).Select(row => row.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return;
        }

        await db.WebPushDeliveries.Where(row => ids.Contains(row.WebPushSubscriptionId)).ExecuteDeleteAsync(ct);
        await db.WebPushSubscriptions.Where(row => ids.Contains(row.Id)).ExecuteDeleteAsync(ct);
    }

    private static string HashCode(string normalized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));

    private static string InvalidMessage(KidDeviceRequest request) =>
        request.Name?.Trim().Length is null or 0 or > MaxNameLength
            ? $"Device names must be 1 to {MaxNameLength} characters."
            : "Only active kid profiles can be assigned to a device.";

    private async Task<Dictionary<int, DateTimeOffset>> ActiveCodesAsync(CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        return (await db.KidPairingCodes.AsNoTracking().ToListAsync(ct))
            .Where(code => code.ExpiresAt > now)
            .ToDictionary(code => code.KidDeviceId, code => code.ExpiresAt);
    }

    private async Task<KidDevice?> FindByTokenAsync(string? token, CancellationToken ct)
    {
        int separator = token?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (token is null
            || separator < 1
            || !int.TryParse(token.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
        {
            return null;
        }

        byte[] secret = new byte[TokenBytes];
        if (!Base64Url.TryDecodeFromChars(token.AsSpan(separator + 1), secret, out int written) || written != TokenBytes)
        {
            return null;
        }

        KidDevice? device = await db.KidDevices.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id && row.TokenHash != null, ct);
        byte[] expected = device?.TokenHash is string hash ? Convert.FromHexString(hash) : NoMatch;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), expected) || device is null)
        {
            return null;
        }

        DateTimeOffset now = time.GetUtcNow();
        if (device.LastSeenAt is not DateTimeOffset lastSeen || now - lastSeen >= ParentSessions.TouchInterval)
        {
            await db.KidDevices
                .Where(row => row.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastSeenAt, now), ct);
            device.LastSeenAt = now;
        }

        return device;
    }

    private void WriteCookie(HttpContext context, string value) =>
        context.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Expires = time.GetUtcNow() + options.Value.DeviceCookieLifetime,
            IsEssential = true,
            Path = "/",
        });

    private async Task<(string Name, int[] ProfileIds)?> ValidateAsync(KidDeviceRequest request, CancellationToken ct)
    {
        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return null;
        }

        int[] profileIds = [.. (request.ProfileIds ?? []).Distinct()];
        int known = await db.KidProfiles.CountAsync(profile => profileIds.Contains(profile.Id) && profile.ArchivedAt == null, ct);
        return known == profileIds.Length ? (name, profileIds) : null;
    }
}

public sealed record KidDeviceRequest(string? Name, IReadOnlyList<int>? ProfileIds);

public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt);

public sealed record KidPairRequest(string? Code);

public sealed record KidDeviceStatus(string Name);

public sealed record KidDeviceSummary(
    int Id,
    string Name,
    IReadOnlyList<int> ProfileIds,
    bool IsPaired,
    DateTimeOffset? PairingCodeExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PairedAt,
    DateTimeOffset? LastSeenAt,
    bool IsCurrent)
{
    public static KidDeviceSummary From(KidDevice device, DateTimeOffset? pairingCodeExpiresAt, bool isCurrent) => new(
        device.Id,
        device.Name,
        [.. device.Profiles.Select(assignment => assignment.KidProfileId).Order()],
        device.TokenHash is not null,
        pairingCodeExpiresAt,
        device.CreatedAt,
        device.PairedAt,
        device.LastSeenAt,
        isCurrent);
}

public sealed record KidDevicePairing(KidDeviceSummary Device, string Code, DateTimeOffset ExpiresAt);

public sealed record DeviceOutcome(KidDeviceSummary? Device, PairingCode? Code, int Status, string? Error)
{
    public static DeviceOutcome Ok(KidDeviceSummary device, PairingCode? code = null) => new(device, code, StatusCodes.Status200OK, null);

    public static DeviceOutcome Invalid(string error) => new(null, null, StatusCodes.Status400BadRequest, error);

    public static DeviceOutcome NotFound() => new(null, null, StatusCodes.Status404NotFound, "Device not found.");
}
