using System.Globalization;
using System.Security.Claims;
using Wishlistarr.Api.Kids;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public static class WebPushEndpoints
{
    public static void MapParentWebPush(this RouteGroupBuilder notifications)
    {
        notifications.MapGet("/web-push", async (HttpContext context, NotificationSettings settings, WebPushCatalog catalog, CancellationToken ct) =>
        {
            int? parentId = await settings.AccountAsync(context.User, ct);
            return parentId is null
                ? Results.Forbid()
                : Results.Ok(new { publicKey = await catalog.PublicKeyAsync(ct), subscriptions = await catalog.ListParentAsync(parentId.Value, ct) });
        })
            .WithName("GetWebPushSubscriptions")
            .WithSummary("Returns this application's web push public key and the signed-in parent's browsers, without endpoints or encryption keys.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden);

        notifications.MapPost("/web-push", async (WebPushInput? input, HttpContext context, NotificationSettings settings, WebPushCatalog catalog, CancellationToken ct) =>
        {
            int? parentId = await settings.AccountAsync(context.User, ct);
            if (parentId is null)
            {
                return Results.Forbid();
            }

            WebPushSave saved = await catalog.SaveParentAsync(parentId.Value, input, ct);
            return saved.Error is null
                ? Results.Json(new { id = saved.Id }, statusCode: saved.Status)
                : Results.Problem(statusCode: saved.Status, title: saved.Error);
        })
            .WithName("SaveWebPushSubscription")
            .WithSummary("Subscribes the signed-in parent's browser to web push, or updates the keys for an endpoint it already saved.")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        notifications.MapDelete("/web-push/{id}", async (int id, HttpContext context, NotificationSettings settings, WebPushCatalog catalog, CancellationToken ct) =>
        {
            int? parentId = await settings.AccountAsync(context.User, ct);
            if (parentId is null)
            {
                return Results.Forbid();
            }

            return await catalog.DeleteParentAsync(parentId.Value, id, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: WebPushCatalog.NotFound);
        })
            .WithName("DeleteWebPushSubscription")
            .WithSummary("Removes one of the signed-in parent's web push browsers.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public static void MapKidWebPush(this RouteGroupBuilder paired)
    {
        paired.MapGet("/web-push", async (HttpContext context, KidDevices devices, WebPushCatalog catalog, CancellationToken ct) =>
        {
            KidDevice? device = await devices.FindCurrentAsync(context, ct);
            return device is null
                ? Results.Unauthorized()
                : Results.Ok(new { publicKey = await catalog.PublicKeyAsync(ct), subscribed = await catalog.DeviceSubscribedAsync(device.Id, ct) });
        })
            .WithName("GetKidWebPush")
            .WithSummary("Returns the web push public key and whether this paired device is subscribed for ready notices.")
            .Produces(StatusCodes.Status200OK);

        paired.MapPost("/web-push", async (WebPushInput? input, HttpContext context, KidDevices devices, WebPushCatalog catalog, CancellationToken ct) =>
        {
            KidDevice? device = await devices.FindCurrentAsync(context, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }

            WebPushSave saved = await catalog.SaveDeviceAsync(device.Id, input, ct);
            return saved.Error is null ? Results.Json(new { id = saved.Id }, statusCode: saved.Status) : Results.Problem(statusCode: saved.Status, title: saved.Error);
        })
            .WithName("SaveKidWebPush")
            .WithSummary("Subscribes this paired device for ready notices. The subscription belongs to the device.")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        paired.MapDelete("/web-push", async (HttpContext context, KidDevices devices, WebPushCatalog catalog, CancellationToken ct) =>
        {
            KidDevice? device = await devices.FindCurrentAsync(context, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }

            await catalog.DeleteDeviceAsync(device.Id, ct);
            return Results.NoContent();
        })
            .WithName("DeleteKidWebPush")
            .WithSummary("Stops ready notices for this paired device.")
            .Produces(StatusCodes.Status204NoContent);
    }

    public static void MapKidReadyNotices(this RouteGroupBuilder signedIn)
    {
        signedIn.MapGet("/ready-notices", async (HttpContext context, WebPushNotices notices, CancellationToken ct) =>
        {
            int? profileId = ProfileId(context.User);
            return profileId is null ? Results.Unauthorized() : Results.Ok(await notices.ListAsync(profileId.Value, ct));
        })
            .WithName("ListKidReadyNotices")
            .WithSummary("Lists ready-to-watch notices for the signed-in profile. A title is included only when that profile is allowed to see it.")
            .Produces<IReadOnlyList<KidReadyNoticeView>>();

        signedIn.MapPost("/ready-notices/{id}/dismiss", async (int id, HttpContext context, WebPushNotices notices, CancellationToken ct) =>
        {
            int? profileId = ProfileId(context.User);
            if (profileId is null)
            {
                return Results.Unauthorized();
            }

            return await notices.DismissAsync(profileId.Value, id, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "That notice was not found.");
        })
            .WithName("DismissKidReadyNotice")
            .WithSummary("Dismisses one ready notice belonging to the signed-in profile.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static int? ProfileId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;
}
