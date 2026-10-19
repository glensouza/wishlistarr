using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Security;

public sealed class ParentSessions(WishlistarrDbContext db, IOptions<AuthOptions> options, TimeProvider time)
{
    public const int MaxUserAgentLength = 256;

    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task SignInAsync(HttpContext context, ParentAccount account, CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        List<ParentSession> existing = await db.ParentSessions
            .Where(session => session.ParentAccountId == account.Id)
            .ToListAsync(ct);
        db.ParentSessions.RemoveRange(existing.Where(session => session.ExpiresAt <= now));

        string userAgent = context.Request.Headers.UserAgent.ToString();
        ParentSession created = new()
        {
            Id = Base64Url(RandomNumberGenerator.GetBytes(32)),
            ParentAccountId = account.Id,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.Value.SessionLifetime,
            UserAgent = userAgent.Length > MaxUserAgentLength ? userAgent[..MaxUserAgentLength] : userAgent,
            RemoteAddress = context.Connection.RemoteIpAddress?.ToString(),
        };
        db.ParentSessions.Add(created);
        await db.SaveChangesAsync(ct);

        ClaimsPrincipal principal = Principals.CreateParent(
            account.Id.ToString(CultureInfo.InvariantCulture),
            account.Username,
            AuthSchemes.Cookie,
            created.Id);
        await context.SignInAsync(
            AuthSchemes.Cookie,
            principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = created.ExpiresAt, AllowRefresh = false });
    }

    public async Task<bool> ValidateAsync(string sessionId, string accountId, CancellationToken ct = default)
    {
        ParentSession? session = await db.ParentSessions.FirstOrDefaultAsync(row => row.Id == sessionId, ct);
        DateTimeOffset now = time.GetUtcNow();
        if (session is null
            || session.ExpiresAt <= now
            || session.ParentAccountId.ToString(CultureInfo.InvariantCulture) != accountId)
        {
            return false;
        }

        if (now - session.LastSeenAt >= TouchInterval)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(
        int accountId,
        string? currentSessionId,
        CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        List<ParentSession> sessions = await db.ParentSessions
            .AsNoTracking()
            .Where(session => session.ParentAccountId == accountId)
            .ToListAsync(ct);
        return sessions
            .Where(session => session.ExpiresAt > now)
            .OrderByDescending(session => session.LastSeenAt)
            .Select(session => new SessionSummary(
                session.Id,
                session.CreatedAt,
                session.LastSeenAt,
                session.ExpiresAt,
                session.UserAgent,
                session.RemoteAddress,
                session.Id == currentSessionId))
            .ToList();
    }

    public async Task<bool> RevokeAsync(int accountId, string sessionId, CancellationToken ct = default) =>
        await db.ParentSessions
            .Where(session => session.ParentAccountId == accountId && session.Id == sessionId)
            .ExecuteDeleteAsync(ct) > 0;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

public sealed record SessionSummary(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    string? UserAgent,
    string? RemoteAddress,
    bool IsCurrent);
