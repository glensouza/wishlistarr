using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Security;

public sealed class ParentAccounts(
    WishlistarrDbContext db,
    IPasswordHasher<ParentAccount> hasher,
    SetupState setup,
    IOptions<AuthOptions> options,
    TimeProvider time)
{
    public const int MaxUsernameLength = 100;
    public const int MinPasswordLength = 10;
    public const int MaxPasswordLength = 256;

    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static readonly ParentAccount DummyAccount = new() { Username = string.Empty, PasswordHash = string.Empty };
    private static readonly string DummyHash =
        new PasswordHasher<ParentAccount>().HashPassword(DummyAccount, Guid.NewGuid().ToString());

    public static string? Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > MaxUsernameLength)
        {
            return $"Usernames must be 1 to {MaxUsernameLength} characters.";
        }

        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            return $"Passwords must be {MinPasswordLength} to {MaxPasswordLength} characters.";
        }

        return null;
    }

    public async Task LoadSetupStateAsync(CancellationToken ct = default)
    {
        if (await db.ParentAccounts.AnyAsync(ct))
        {
            setup.MarkComplete();
        }
    }

    public async Task<ParentAccount?> CreateFirstAsync(string username, string password, CancellationToken ct = default)
    {
        await SetupLock.WaitAsync(ct);
        try
        {
            if (setup.IsComplete || await db.ParentAccounts.AnyAsync(ct))
            {
                setup.MarkComplete();
                return null;
            }

            ParentAccount account = new()
            {
                Username = username.Trim(),
                PasswordHash = string.Empty,
                CreatedAt = time.GetUtcNow(),
            };
            account.PasswordHash = hasher.HashPassword(account, password);
            db.ParentAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            setup.MarkComplete();
            return account;
        }
        finally
        {
            SetupLock.Release();
        }
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (password.Length > MaxPasswordLength)
        {
            return new LoginResult(LoginOutcome.InvalidCredentials, null, null);
        }

        string trimmed = username.Trim();
        ParentAccount? account = await db.ParentAccounts.FirstOrDefaultAsync(row => row.Username == trimmed, ct);
        if (account is null)
        {
            hasher.VerifyHashedPassword(DummyAccount, DummyHash, password);
            return new LoginResult(LoginOutcome.InvalidCredentials, null, null);
        }

        DateTimeOffset now = time.GetUtcNow();
        if (account.LockoutEndsAt is DateTimeOffset lockedUntil)
        {
            if (lockedUntil > now)
            {
                return new LoginResult(LoginOutcome.LockedOut, null, lockedUntil);
            }

            account.LockoutEndsAt = null;
            account.FailedLoginCount = 0;
        }

        PasswordVerificationResult result = hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            account.FailedLoginCount++;
            if (account.FailedLoginCount >= options.Value.MaxFailedLogins)
            {
                account.FailedLoginCount = 0;
                account.LockoutEndsAt = now + options.Value.LockoutDuration;
            }

            await db.SaveChangesAsync(ct);
            return account.LockoutEndsAt is DateTimeOffset until
                ? new LoginResult(LoginOutcome.LockedOut, null, until)
                : new LoginResult(LoginOutcome.InvalidCredentials, null, null);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = hasher.HashPassword(account, password);
        }

        account.FailedLoginCount = 0;
        await db.SaveChangesAsync(ct);
        return new LoginResult(LoginOutcome.Succeeded, account, null);
    }
}

public enum LoginOutcome
{
    Succeeded,
    InvalidCredentials,
    LockedOut,
}

public sealed record LoginResult(LoginOutcome Outcome, ParentAccount? Account, DateTimeOffset? LockedUntil);
