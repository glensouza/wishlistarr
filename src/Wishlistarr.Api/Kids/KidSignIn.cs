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

    public async Task<KidSignInResult> VerifyAsync(int profileId, string? pin, string deviceHash, CancellationToken ct = default)
    {
        await AttemptLock.WaitAsync(ct);
        try
        {
            return await this.VerifyLockedAsync(profileId, pin, deviceHash, ct);
        }
        finally
        {
            AttemptLock.Release();
        }
    }

    private async Task<KidSignInResult> VerifyLockedAsync(int profileId, string? pin, string deviceHash, CancellationToken ct)
    {
        KidOptions config = options.Value;
        DateTimeOffset now = time.GetUtcNow();
        KidProfile? profile = await db.KidProfiles.FirstOrDefaultAsync(row => row.Id == profileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            hasher.VerifyHashedPassword(DummyProfile, DummyHash, pin ?? DummyPin);
            return KidSignInResult.Invalid();
        }

        if (profile.LockedAt is not null)
        {
            return KidSignInResult.ProfileLocked();
        }

        KidPinAttempt? attempt = await db.KidPinAttempts
            .FirstOrDefaultAsync(row => row.KidProfileId == profile.Id && row.DeviceHash == deviceHash, ct);
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

            profile.FailedPinCount = 0;
            profile.LastFailedPinAt = null;
            await db.SaveChangesAsync(ct);
            return KidSignInResult.Success(profile);
        }

        if (attempt is null)
        {
            attempt = new KidPinAttempt { KidProfileId = profile.Id, DeviceHash = deviceHash };
            db.KidPinAttempts.Add(attempt);
        }
        else if (now - attempt.LastFailedAt >= config.FailureResetAfter)
        {
            attempt.FailedCount = 0;
        }

        if (profile.LastFailedPinAt is DateTimeOffset lastProfileFailure && now - lastProfileFailure >= config.FailureResetAfter)
        {
            profile.FailedPinCount = 0;
        }

        attempt.FailedCount++;
        attempt.LastFailedAt = now;
        attempt.NextAttemptAt = now + config.GetBackoff(attempt.FailedCount);
        profile.FailedPinCount++;
        profile.LastFailedPinAt = now;
        bool locked = profile.FailedPinCount >= config.LockThreshold;
        if (locked)
        {
            profile.LockedAt = now;
        }

        await db.SaveChangesAsync(ct);

        if (locked)
        {
            await notifier.NotifyAsync(
                new ParentNotification(
                    ParentNotificationKinds.ProfileLocked,
                    $"{profile.Name} was locked after {profile.FailedPinCount} wrong PINs. Unlock the profile to let them sign in again."),
                ct);
        }
        else if (attempt.FailedCount == config.NotifyThreshold)
        {
            await notifier.NotifyAsync(
                new ParentNotification(
                    ParentNotificationKinds.PinFailures,
                    $"{attempt.FailedCount} wrong PINs in a row were entered for {profile.Name} on one device."),
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
