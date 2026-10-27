using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public sealed record ApprovalLinkPayload(Guid Id, int ParentId, int RequestId, string Action, long Expires);

public sealed class NotificationLinks(WishlistarrDbContext db, IDataProtectionProvider protection, TimeProvider time, NotificationSettings settings)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);
    private readonly IDataProtector protector = protection.CreateProtector("Wishlistarr.Notifications.Approval.v1");

    public async Task<NotificationMessage> AddAsync(string body, int parentId, int requestId, CancellationToken ct)
    {
        string baseUrl = await settings.BaseUrlAsync(ct);
        if (baseUrl.Length == 0)
        {
            throw new NotificationDeliveryException("Save the public Wishlistarr URL to send approval links.");
        }

        NotificationApproval? grant = await db.NotificationApprovals
            .FirstOrDefaultAsync(row => row.ParentAccountId == parentId && row.MediaRequestId == requestId, ct);
        if (grant is null)
        {
            grant = new NotificationApproval
            {
                Id = Guid.NewGuid(),
                ParentAccountId = parentId,
                MediaRequestId = requestId,
                ExpiresAt = time.GetUtcNow() + Lifetime,
            };
            db.NotificationApprovals.Add(grant);
            await db.SaveChangesAsync(ct);
        }

        if (grant.UsedAt is not null || grant.ExpiresAt <= time.GetUtcNow())
        {
            return new NotificationMessage($"{body}\nOpen Wishlistarr to review this request: {baseUrl}/parent");
        }

        return new NotificationMessage(body, this.Url(baseUrl, grant, "approve"), this.Url(baseUrl, grant, "deny"));
    }

    public async Task<ApprovalLinkPayload?> InspectAsync(string? token, CancellationToken ct)
    {
        if (token is null || token.Length > 2000)
        {
            return null;
        }

        ApprovalLinkPayload payload;
        try
        {
            string[] parts = this.protector.Unprotect(token).Split(':');
            if (parts.Length != 5 || !Guid.TryParseExact(parts[0], "N", out Guid id)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int parentId)
                || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out int requestId)
                || parts[3] is not ("approve" or "deny") || !long.TryParse(parts[4], CultureInfo.InvariantCulture, out long expires))
            {
                return null;
            }

            payload = new ApprovalLinkPayload(id, parentId, requestId, parts[3], expires);
        }
        catch (CryptographicException)
        {
            return null;
        }

        NotificationApproval? grant = await db.NotificationApprovals.AsNoTracking().FirstOrDefaultAsync(row => row.Id == payload.Id, ct);
        if (grant is null || grant.ParentAccountId != payload.ParentId || grant.MediaRequestId != payload.RequestId
            || grant.UsedAt is not null || grant.ExpiresAt <= time.GetUtcNow() || grant.ExpiresAt.ToUnixTimeSeconds() != payload.Expires)
        {
            return null;
        }

        return await db.MediaRequests.AnyAsync(row => row.Id == payload.RequestId && row.State == RequestState.Pending, ct) ? payload : null;
    }

    public async Task<DecisionItem?> DecideAsync(string? token, int parentId, RequestApproval approval, CancellationToken ct)
    {
        ApprovalLinkPayload? payload = await this.InspectAsync(token, ct);
        if (payload is null || payload.ParentId != parentId)
        {
            return null;
        }

        ParentAccount? account = await db.ParentAccounts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == parentId, ct);
        if (account is null)
        {
            return null;
        }

        DateTimeOffset now = time.GetUtcNow();
        // Both choices share one grant, so a second click cannot race an opposite decision.
        int claimed = await db.NotificationApprovals.Where(row => row.Id == payload.Id && row.UsedAt == null
                && row.ExpiresAt > now && row.ParentAccountId == parentId && row.MediaRequestId == payload.RequestId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UsedAt, now), ct);
        if (claimed != 1)
        {
            return null;
        }

        RequestActor actor = RequestActor.Parent(account.Id, account.Username);
        return payload.Action == "approve"
            ? await approval.ApproveAsync(payload.RequestId, actor, ct: CancellationToken.None)
            : (await approval.DenyManyAsync([payload.RequestId], actor, null, CancellationToken.None))[0];
    }

    private string Url(string baseUrl, NotificationApproval grant, string action)
    {
        string payload = string.Create(CultureInfo.InvariantCulture, $"{grant.Id:N}:{grant.ParentAccountId}:{grant.MediaRequestId}:{action}:{grant.ExpiresAt.ToUnixTimeSeconds()}");
        return $"{baseUrl}/notifications/confirm?token={Uri.EscapeDataString(this.protector.Protect(payload))}";
    }
}
