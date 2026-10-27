using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Security;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public static class NotificationEndpoints
{
    public static void MapNotificationSettings(this RouteGroupBuilder v1)
    {
        RouteGroupBuilder group = v1.MapGroup("/notifications").WithTags("Notifications");
        group.MapGet("/", async (HttpContext context, NotificationSettings settings, CancellationToken ct) =>
        {
            int? id = await settings.AccountAsync(context.User, ct);
            return id is null ? Results.Forbid() : Results.Ok(new
            {
                publicBaseUrl = await settings.BaseUrlAsync(ct),
                providers = await settings.ListAsync(id.Value, ct),
            });
        });

        group.MapPut("/base-url", async (NotificationBaseUrl update, NotificationSettings settings, CancellationToken ct) =>
            await settings.SaveBaseUrlAsync(update.Url, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: 400, title: "Use the public HTTP or HTTPS origin, without a path, query, credentials or fragment (up to 256 characters)."));

        group.MapPut("/{provider}", async (string provider, NotificationUpdate update, HttpContext context, NotificationSettings settings, CancellationToken ct) =>
        {
            int? id = await settings.AccountAsync(context.User, ct);
            if (id is null)
            {
                return Results.Forbid();
            }

            string? error = await settings.SaveAsync(id.Value, provider, update, ct);
            return error is null ? Results.NoContent() : Results.Problem(statusCode: 400, title: error);
        });

        group.MapPost("/{provider}/test", async (string provider, HttpContext context, NotificationSettings settings, NotificationSender sender, ILogger<NotificationSender> logger, CancellationToken ct) =>
        {
            int? id = await settings.AccountAsync(context.User, ct);
            if (id is null)
            {
                return Results.Forbid();
            }

            NotificationSubscription? subscription = await settings.FindAsync(id.Value, provider, ct);
            if (subscription is null)
            {
                return Results.Problem(statusCode: 400, title: "Save this provider before sending a test.");
            }

            try
            {
                await sender.SendAsync(provider, settings.Read(subscription), new NotificationMessage("Wishlistarr test notification."), ct);
                return Results.NoContent();
            }
            catch (NotificationDeliveryException exception)
            {
                logger.LogWarning("Notification test through {Provider} failed", subscription.Provider);
                return Results.Problem(statusCode: 502, title: exception.Message);
            }
        });
    }

    public static void MapNotificationConfirmation(this IEndpointRouteBuilder app)
    {
        app.MapGet("/notifications/confirm", async (string? token, HttpContext context, NotificationLinks links, WishlistarrDbContext db, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            ApprovalLinkPayload? payload = await links.InspectAsync(token, ct);
            if (payload is null)
            {
                return Page(context, "Link unavailable", "This link has expired, was used, or the request has already been decided. Open Wishlistarr to review the request.", status: 410);
            }

            if (!Recipient(context, payload.ParentId))
            {
                string reload = WebUtility.HtmlEncode($"/notifications/confirm?token={Uri.EscapeDataString(token!)}");
                return Page(context, "Sign in to confirm", "Sign in as the parent who received this notification. Then return here and continue.",
                    $"<p><a href=\"/parent\" target=\"_blank\" rel=\"noopener noreferrer\">Open parent sign-in</a></p><p><a href=\"{reload}\">Continue after signing in</a></p>");
            }

            AntiforgeryTokenSet csrf = antiforgery.GetAndStoreTokens(context);
            string? description = await db.MediaRequests.Where(row => row.Id == payload.RequestId)
                .Select(row => (row.Title ?? "Untitled request") + " for " + row.KidProfile!.Name).SingleOrDefaultAsync(ct);
            string action = payload.Action == "approve" ? "Approve" : "Deny";
            string form = $"<form method=\"post\" action=\"/notifications/confirm\"><input type=\"hidden\" name=\"token\" value=\"{WebUtility.HtmlEncode(token)}\"><input type=\"hidden\" name=\"{WebUtility.HtmlEncode(csrf.FormFieldName)}\" value=\"{WebUtility.HtmlEncode(csrf.RequestToken)}\"><button type=\"submit\">{action} request #{payload.RequestId}</button></form>";
            return Page(context, $"{action} request?", $"Confirm that you want to {payload.Action} request #{payload.RequestId}: {description}. Opening this page has not changed it.", form);
        }).AllowAnonymous();

        app.MapPost("/notifications/confirm", async (HttpContext context, NotificationLinks links, RequestApproval approval, CancellationToken ct) =>
        {
            if (!Principals.TryGetSessionAccount(context.User, out int parentId, out _)
                || context.User.Identity?.AuthenticationType != AuthSchemes.Cookie)
            {
                return Results.Forbid();
            }

            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest();
            }

            IFormCollection form = await context.Request.ReadFormAsync(ct);
            DecisionItem? result = await links.DecideAsync(form["token"], parentId, approval, ct);
            return result is null
                ? Page(context, "Link unavailable", "This link is invalid for this parent, expired, already used, or the request has already been decided.", status: 410)
                : result.Status is DecisionStatus.Succeeded or DecisionStatus.AlreadyDecided
                    ? Page(context, "Decision saved", $"Request #{result.Id} is {result.State}. This link can no longer be used.")
                    : Page(context, "Decision could not complete", "This link has been used. Open the parent area to check the request and retry if needed.", status: 409);
        }).RequireAuthorization(Policies.Parent);
    }

    private static bool Recipient(HttpContext context, int recipient) =>
        context.User.Identity?.AuthenticationType == AuthSchemes.Cookie
        && Principals.TryGetSessionAccount(context.User, out int id, out _) && id == recipient;

    private static IResult Page(HttpContext context, string title, string message, string extra = "", int status = 200)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Content($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{WebUtility.HtmlEncode(title)} · Wishlistarr</title></head><body><main><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p>{extra}<p><a href=\"/parent\">Parent area</a></p></main></body></html>", "text/html", statusCode: status);
    }
}

public sealed record NotificationBaseUrl(string? Url);
