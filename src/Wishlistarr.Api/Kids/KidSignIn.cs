using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.Kids;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Kids;

public sealed class KidSignIn(
    WishlistarrDbContext db,
    IPasswordHasher<KidProfile> hasher,
    IParentNotifier notifier,
    IOptions<KidOptions> options,
    TimeProvider time)
{
    private const string DummyPin = "0000";

    private static readonly SemaphoreSlim AttemptLock = new(1, 1);
    private static readonly KidProfile DummyProfile = new() { Name = string.Empty, Avatar = string.Empty, PlexLabel = string.Empty };
    private static readonly string DummyHash = new PasswordHasher<KidProfile>().HashPassword(DummyProfile, DummyPin);

    public async Task<KidSignInResult> VerifyAsync(int profileId, string? pin, int deviceId, CancellationToken ct = default)
    {
        await AttemptLock.WaitAsync(ct);
        try
        {
            return await this.VerifyLockedAsync(profileId, pin, deviceId, ct);
        }
        finally
        {
            AttemptLock.Release();
        }
    }

    private async Task<KidSignInResult> VerifyLockedAsync(int profileId, string? pin, int deviceId, CancellationToken ct)
    {
        KidOptions config = options.Value;
        DateTimeOffset now = time.GetUtcNow();
        KidProfile? profile = await db.KidProfiles.FirstOrDefaultAsync(
            row => row.Id == profileId
                && row.ArchivedAt == null
                && db.KidDeviceProfiles.Any(assignment => assignment.KidDeviceId == deviceId && assignment.KidProfileId == row.Id),
            ct);
        if (profile is null)
        {
            hasher.VerifyHashedPassword(DummyProfile, DummyHash, pin ?? DummyPin);
            return KidSignInResult.Invalid();
        }

        KidPinAttempt? attempt = await db.KidPinAttempts
            .FirstOrDefaultAsync(row => row.KidProfileId == profile.Id && row.KidDeviceId == deviceId, ct);
        if (profile.LockedAt is not null || attempt?.LockedAt is not null)
        {
            return KidSignInResult.ProfileLocked();
        }

        if (attempt is not null && attempt.NextAttemptAt > now)
        {
            return KidSignInResult.Backoff(attempt.NextAttemptAt);
        }

        if (profile.PinHash is null)
        {
            return KidSignInResult.Success(profile);
        }

        if (pin is null)
        {
            return KidSignInResult.Invalid();
        }

        PasswordVerificationResult result = hasher.VerifyHashedPassword(profile, profile.PinHash, pin);
        if (result != PasswordVerificationResult.Failed)
        {
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                profile.PinHash = hasher.HashPassword(profile, pin);
            }

            if (attempt is not null)
            {
                db.KidPinAttempts.Remove(attempt);
            }

            await db.SaveChangesAsync(ct);
            return KidSignInResult.Success(profile);
        }

        if (attempt is null)
        {
            attempt = new KidPinAttempt { KidProfileId = profile.Id, KidDeviceId = deviceId };
            db.KidPinAttempts.Add(attempt);
        }
        else if (now - attempt.LastFailedAt >= config.FailureResetAfter)
        {
            attempt.FailedCount = 0;
        }

        attempt.FailedCount++;
        attempt.LastFailedAt = now;
        attempt.NextAttemptAt = now + config.GetBackoff(attempt.FailedCount);
        bool locked = attempt.FailedCount >= config.LockThreshold;
        if (locked)
        {
            attempt.LockedAt = now;
        }

        await db.SaveChangesAsync(ct);

        if (locked || attempt.FailedCount == config.NotifyThreshold)
        {
            string deviceName = await db.KidDevices
                .Where(device => device.Id == deviceId)
                .Select(device => device.Name)
                .FirstOrDefaultAsync(ct) ?? "a device that has since been removed";
            await notifier.NotifyAsync(
                locked
                    ? new ParentNotification(
                        ParentNotificationKinds.ProfileLocked,
                        $"{profile.Name} was locked on {deviceName} after {attempt.FailedCount} wrong PINs. Unlock the profile to let them sign in there again.")
                    : new ParentNotification(
                        ParentNotificationKinds.PinFailures,
                        $"{attempt.FailedCount} wrong PINs in a row were entered for {profile.Name} on {deviceName}."),
                ct);
        }

        return KidSignInResult.Invalid();
    }
}

public enum KidSignInOutcome
{
    Succeeded,
    InvalidPin,
    BackedOff,
    Locked,
}

public sealed record KidSignInResult(KidSignInOutcome Outcome, KidProfile? Profile, DateTimeOffset? RetryAt)
{
    public static KidSignInResult Success(KidProfile profile) => new(KidSignInOutcome.Succeeded, profile, null);

    public static KidSignInResult Invalid() => new(KidSignInOutcome.InvalidPin, null, null);

    public static KidSignInResult Backoff(DateTimeOffset retryAt) => new(KidSignInOutcome.BackedOff, null, retryAt);

    public static KidSignInResult ProfileLocked() => new(KidSignInOutcome.Locked, null, null);
}
