using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Security;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Kids;

public sealed class KidSessions(WishlistarrDbContext db, IOptions<KidOptions> options, TimeProvider time)
{
    public async Task SignInAsync(HttpContext context, KidProfile profile, CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        List<KidSession> existing = await db.KidSessions.Where(session => session.KidProfileId == profile.Id).ToListAsync(ct);
        db.KidSessions.RemoveRange(existing.Where(session => session.ExpiresAt <= now));

        string userAgent = context.Request.Headers.UserAgent.ToString();
        KidSession created = new()
        {
            Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('='),
            KidProfileId = profile.Id,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.Value.SessionLifetime,
            UserAgent = userAgent.Length > ParentSessions.MaxUserAgentLength ? userAgent[..ParentSessions.MaxUserAgentLength] : userAgent,
            RemoteAddress = context.Connection.RemoteIpAddress?.ToString(),
        };
        db.KidSessions.Add(created);
        await db.SaveChangesAsync(ct);

        ClaimsPrincipal principal = Principals.CreateKid(
            profile.Id.ToString(CultureInfo.InvariantCulture),
            profile.Name,
            AuthSchemes.KidCookie,
            created.Id);
        await context.SignInAsync(
            AuthSchemes.KidCookie,
            principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = created.ExpiresAt, AllowRefresh = false });
    }

    public async Task<bool> ValidateAsync(string sessionId, string profileId, CancellationToken ct = default)
    {
        KidSession? session = await db.KidSessions.Include(row => row.KidProfile).FirstOrDefaultAsync(row => row.Id == sessionId, ct);
        DateTimeOffset now = time.GetUtcNow();
        if (session is null
            || session.ExpiresAt <= now
            || session.KidProfile is not { ArchivedAt: null, LockedAt: null }
            || session.KidProfileId.ToString(CultureInfo.InvariantCulture) != profileId)
        {
            return false;
        }

        if (now - session.LastSeenAt >= ParentSessions.TouchInterval)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task RevokeAsync(int profileId, string sessionId, CancellationToken ct = default) =>
        await db.KidSessions
            .Where(session => session.KidProfileId == profileId && session.Id == sessionId)
            .ExecuteDeleteAsync(ct);
}
