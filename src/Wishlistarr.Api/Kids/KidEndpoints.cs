using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Search;
using Wishlistarr.Api.Security;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Kids;

public static class KidEndpoints
{
    public static void MapKid(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder kid = app.MapGroup("/api/v1/kid").WithTags("Kid");

        kid.MapPost("/pair", async (KidPairRequest? request, HttpContext context, KidDevices devices, CancellationToken ct) =>
        {
            KidDevice? device = await devices.RedeemAsync(context, request?.Code, ct);
            return device is null
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That pairing code is not valid or has expired.")
                : Results.Ok(new KidDeviceStatus(device.Name));
        })
            .AllowAnonymous()
            .RequireRateLimiting(SecuritySetup.LoginRateLimitPolicy)
            .WithName("PairKidDevice")
            .WithSummary("Pairs this browser as a kid device with a one-time code from a parent and sets the device cookie.")
            .Produces<KidDeviceStatus>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        RouteGroupBuilder paired = kid.MapGroup("/").RequireAuthorization(Policies.KidDevice)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        paired.MapGet("/profiles", async (HttpContext context, KidDevices devices, CancellationToken ct) =>
        {
            KidDevice device = await RequireDeviceAsync(context, devices, ct);
            return (await devices.ListProfilesAsync(device.Id, ct)).Select(profile => new KidPickerEntry(
                profile.Id,
                profile.Name,
                profile.Avatar,
                profile.PinHash is not null));
        })
            .WithName("ListKidPickerProfiles")
            .WithSummary("Lists the profiles assigned to this paired device for the sign-in picker.")
            .Produces<IEnumerable<KidPickerEntry>>();

        paired.MapPost("/signin", async (
            KidSignInRequest? request,
            HttpContext context,
            KidSignIn signIn,
            KidSessions sessions,
            KidDevices devices,
            ParentSessions parentSessions,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (request is not { ProfileId: int profileId })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A profile is required.");
            }

            if (request.Pin is not null && !KidPins.IsValid(request.Pin))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"PINs are {KidPins.MinLength} to {KidPins.MaxLength} digits.");
            }

            KidDevice device = await RequireDeviceAsync(context, devices, ct);
            KidSignInResult result = await signIn.VerifyAsync(profileId, request.Pin, device.Id, ct);
            switch (result.Outcome)
            {
                case KidSignInOutcome.Succeeded when result.Profile is not null:
                    await EndParentSessionAsync(context, parentSessions, ct);
                    await sessions.SignInAsync(context, result.Profile, device.Id, ct);
                    devices.RefreshCookie(context);
                    return Results.Ok(new KidPickerEntry(
                        result.Profile.Id,
                        result.Profile.Name,
                        result.Profile.Avatar,
                        result.Profile.PinHash is not null));
                case KidSignInOutcome.Locked:
                    return Results.Problem(
                        statusCode: StatusCodes.Status423Locked,
                        title: "This profile is locked.",
                        detail: "Ask a parent to unlock it.");
                case KidSignInOutcome.BackedOff when result.RetryAt is DateTimeOffset until:
                    SetRetryAfter(context, until, time);
                    return Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many wrong PINs.",
                        detail: "Wait a moment before trying again.");
                default:
                    return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid PIN.");
            }
        })
            .RequireRateLimiting(SecuritySetup.LoginRateLimitPolicy)
            .WithName("KidSignIn")
            .WithSummary("Signs a kid in on a paired device to one of its profiles with the PIN and starts a cookie session.")
            .Produces<KidPickerEntry>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        paired.MapPost("/exit", async (
            Credentials? credentials,
            HttpContext context,
            SetupState setup,
            ParentAccounts accounts,
            ParentSessions parentSessions,
            KidSessions kidSessions,
            TimeProvider time,
            CancellationToken ct) =>
            await AuthEndpoints.LoginAsync(credentials, context, setup, accounts, parentSessions, time, () => EndKidSessionAsync(context, kidSessions, ct), ct))
            .RequireRateLimiting(SecuritySetup.LoginRateLimitPolicy)
            .WithName("ExitKidMode")
            .WithSummary("Leaves kid mode on a paired device: checks the parent's credentials, ends any kid session and starts a parent session.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        RouteGroupBuilder signedIn = kid.MapGroup("/").RequireAuthorization(Policies.Kid)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        signedIn.MapGet("/me", (HttpContext context) => new KidMeResponse(
            context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty,
            context.User.Identity?.Name ?? string.Empty))
            .WithName("GetKidMe")
            .WithSummary("Returns the signed-in kid profile.")
            .Produces<KidMeResponse>();

        signedIn.MapPost("/signout", async (HttpContext context, KidSessions sessions, CancellationToken ct) =>
        {
            string? sessionId = context.User.FindFirst(Principals.SessionIdClaim)?.Value;
            if (sessionId is not null
                && int.TryParse(
                    context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int profileId))
            {
                await sessions.RevokeAsync(profileId, sessionId, ct);
            }

            await context.SignOutAsync(AuthSchemes.KidCookie);
            return Results.NoContent();
        })
            .WithName("KidSignOut")
            .WithSummary("Ends the current kid session.")
            .Produces(StatusCodes.Status204NoContent);

        signedIn.MapKidRequests();
        signedIn.MapKidSearch();
    }

    public static void MapParentKidProfiles(this RouteGroupBuilder parent)
    {
        RouteGroupBuilder profiles = parent.MapGroup("/kid-profiles").WithTags("Kid profiles");

        profiles.MapGet("/", async (KidProfiles service, CancellationToken ct) =>
        {
            IReadOnlyDictionary<int, DateTimeOffset> locks = await service.ListDeviceLocksAsync(ct);
            return (await service.ListAsync(ct)).Select(profile =>
                KidProfileSummary.From(profile, locks.TryGetValue(profile.Id, out DateTimeOffset lockedAt) ? lockedAt : null));
        })
            .WithName("ListKidProfiles")
            .WithSummary("Lists kid profiles. Archived profiles are not included and PIN hashes are never returned.")
            .Produces<IEnumerable<KidProfileSummary>>();

        profiles.MapPost("/", async (KidProfileRequest? request, KidProfiles service, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A profile is required.");
            }

            ProfileOutcome outcome = await service.CreateAsync(request, ct);
            return outcome.Profile is null
                ? Results.Problem(statusCode: outcome.Status, title: outcome.Error)
                : Results.Created($"/api/v1/kid-profiles/{outcome.Profile.Id}", KidProfileSummary.From(outcome.Profile));
        })
            .WithName("CreateKidProfile")
            .WithSummary("Creates a kid profile. The Plex label defaults to the profile name.")
            .Produces<KidProfileSummary>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        profiles.MapPut("/{id}", async (int id, KidProfileRequest? request, KidProfiles service, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A profile is required.");
            }

            ProfileOutcome outcome = await service.UpdateAsync(id, request, ct);
            return outcome.Profile is null
                ? Results.Problem(statusCode: outcome.Status, title: outcome.Error)
                : Results.Ok(KidProfileSummary.From(outcome.Profile));
        })
            .WithName("UpdateKidProfile")
            .WithSummary("Replaces a kid profile. Omit the PIN to keep it, or set removePin to clear it.")
            .Produces<KidProfileSummary>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        profiles.MapDelete("/{id}", async (int id, bool? removeOwnedLabels, KidProfiles service, CancellationToken ct) =>
            await service.ArchiveAsync(id, removeOwnedLabels ?? false, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Profile not found."))
            .WithName("DeleteKidProfile")
            .WithSummary("Archives a kid profile so its history is kept, and records whether its owned Plex labels should be removed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        profiles.MapPost("/{id}/unlock", async (int id, KidProfiles service, CancellationToken ct) =>
            await service.UnlockAsync(id, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Profile not found."))
            .WithName("UnlockKidProfile")
            .WithSummary("Unlocks a locked kid profile and clears its failed PIN counts.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public static async Task ValidateKidSessionAsync(CookieValidatePrincipalContext context)
    {
        string? sessionId = context.Principal?.FindFirst(Principals.SessionIdClaim)?.Value;
        string? profileId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        KidSessions sessions = context.HttpContext.RequestServices.GetRequiredService<KidSessions>();
        KidDevice? device = await context.HttpContext.RequestServices.GetRequiredService<KidDevices>()
            .FindCurrentAsync(context.HttpContext, context.HttpContext.RequestAborted);
        if (sessionId is null
            || profileId is null
            || !await sessions.ValidateAsync(sessionId, profileId, device?.Id, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthSchemes.KidCookie);
        }
    }

    private static async Task<KidDevice> RequireDeviceAsync(HttpContext context, KidDevices devices, CancellationToken ct) =>
        await devices.FindCurrentAsync(context, ct)
            ?? throw new InvalidOperationException("The paired device policy let a request without a device through.");

    private static async Task EndParentSessionAsync(HttpContext context, ParentSessions sessions, CancellationToken ct)
    {
        if (!context.Request.Cookies.ContainsKey(SecuritySetup.CookieName))
        {
            return;
        }

        AuthenticateResult current = await context.AuthenticateAsync(AuthSchemes.Cookie);
        if (current.Principal is ClaimsPrincipal user && Principals.TryGetSessionAccount(user, out int accountId, out string sessionId))
        {
            await sessions.RevokeAsync(accountId, sessionId, ct);
        }

        await context.SignOutAsync(AuthSchemes.Cookie);
    }

    private static async Task EndKidSessionAsync(HttpContext context, KidSessions sessions, CancellationToken ct)
    {
        AuthenticateResult current = await context.AuthenticateAsync(AuthSchemes.KidCookie);
        string? sessionId = current.Principal?.FindFirst(Principals.SessionIdClaim)?.Value;
        if (sessionId is not null
            && int.TryParse(
                current.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int profileId))
        {
            await sessions.RevokeAsync(profileId, sessionId, ct);
        }

        await context.SignOutAsync(AuthSchemes.KidCookie);
    }

    private static void SetRetryAfter(HttpContext context, DateTimeOffset until, TimeProvider time)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling((until - time.GetUtcNow()).TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed record KidSignInRequest(int? ProfileId, string? Pin);

public sealed record KidPickerEntry(int Id, string Name, string Avatar, bool RequiresPin);

public sealed record KidMeResponse(string Id, string Name);

public sealed record KidProfileSummary(
    int Id,
    string Name,
    string Avatar,
    bool HasPin,
    int? MovieAgeLimit,
    int? TvAgeLimit,
    string PlexLabel,
    string? PlexUserId,
    int? WeeklyAllowance,
    bool IsLocked,
    DateTimeOffset? LockedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<int> BlockedGenreIds,
    IReadOnlyList<int> BlockedKeywordIds,
    IReadOnlyList<string> AllowedOriginalLanguages)
{
    public static KidProfileSummary From(KidProfile profile) => From(profile, null);

    public static KidProfileSummary From(KidProfile profile, DateTimeOffset? deviceLockedAt) => new(
        profile.Id,
        profile.Name,
        profile.Avatar,
        profile.PinHash is not null,
        profile.MovieAgeLimit,
        profile.TvAgeLimit,
        profile.PlexLabel,
        profile.PlexUserId,
        profile.WeeklyAllowance,
        profile.LockedAt is not null || deviceLockedAt is not null,
        profile.LockedAt ?? deviceLockedAt,
        profile.CreatedAt,
        profile.BlockedGenreIds,
        profile.BlockedKeywordIds,
        profile.AllowedOriginalLanguages);
}
