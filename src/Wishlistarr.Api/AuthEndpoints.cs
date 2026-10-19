using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Wishlistarr.Api.Security;
using Wishlistarr.Data;

namespace Wishlistarr.Api;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder auth = app.MapGroup("/api/v1/auth").WithTags("Auth").AllowAnonymous();

        auth.MapGet("/status", (HttpContext context, SetupState setup) =>
        {
            string? principalType = Principals.GetPrincipalType(context.User);
            return new AuthStatusResponse(
                !setup.IsComplete,
                principalType,
                principalType is null ? null : context.User.Identity?.Name);
        })
            .WithName("GetAuthStatus")
            .WithSummary("Reports whether first-run setup is required and who is signed in.")
            .Produces<AuthStatusResponse>();

        auth.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
            return new AntiforgeryResponse(tokens.RequestToken ?? string.Empty, SecuritySetup.AntiforgeryHeaderName);
        })
            .WithName("GetAntiforgeryToken")
            .WithSummary("Issues the token that cookie-authenticated writes must send in the antiforgery header.")
            .Produces<AntiforgeryResponse>();

        auth.MapPost("/setup", async (
            Credentials? credentials,
            HttpContext context,
            ParentAccounts accounts,
            ParentSessions sessions,
            CancellationToken ct) =>
        {
            if (credentials is not { Username: string username, Password: string password })
            {
                return CredentialsRequired();
            }

            string? error = ParentAccounts.Validate(username, password);
            if (error is not null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
            }

            ParentAccount? account = await accounts.CreateFirstAsync(username, password, ct);
            if (account is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Setup is already complete.");
            }

            await sessions.SignInAsync(context, account, ct);
            return Results.NoContent();
        })
            .RequireRateLimiting(SecuritySetup.LoginRateLimitPolicy)
            .WithName("SetupParentAccount")
            .WithSummary("Creates the parent account on first run and signs it in.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/login", async (
            Credentials? credentials,
            HttpContext context,
            SetupState setup,
            ParentAccounts accounts,
            ParentSessions sessions,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (credentials is not { Username: string username, Password: string password })
            {
                return CredentialsRequired();
            }

            if (!setup.IsComplete)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Setup required.");
            }

            LoginResult result = await accounts.LoginAsync(username, password, ct);
            switch (result.Outcome)
            {
                case LoginOutcome.Succeeded when result.Account is not null:
                    await sessions.SignInAsync(context, result.Account, ct);
                    return Results.NoContent();
                case LoginOutcome.LockedOut when result.LockedUntil is DateTimeOffset until:
                    int seconds = Math.Max(1, (int)Math.Ceiling((until - time.GetUtcNow()).TotalSeconds));
                    context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                    return Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many failed sign-in attempts.",
                        detail: "Sign-in is locked. Try again later.");
                default:
                    return Results.Problem(
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: "Invalid username or password.");
            }
        })
            .RequireRateLimiting(SecuritySetup.LoginRateLimitPolicy)
            .WithName("Login")
            .WithSummary("Signs a parent in with a username and password and starts a cookie session.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    public static void MapParentAuth(this RouteGroupBuilder parent)
    {
        RouteGroupBuilder auth = parent.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/logout", async (HttpContext context, ParentSessions sessions, CancellationToken ct) =>
        {
            string? sessionId = context.User.FindFirstValue(Principals.SessionIdClaim);
            if (sessionId is not null)
            {
                await sessions.RevokeAsync(sessionId, ct);
                await context.SignOutAsync(AuthSchemes.Cookie);
            }

            return Results.NoContent();
        })
            .WithName("Logout")
            .WithSummary("Ends the current cookie session.")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapGet("/sessions", async (HttpContext context, ParentSessions sessions, CancellationToken ct) =>
            await sessions.ListAsync(context.User.FindFirstValue(Principals.SessionIdClaim), ct))
            .WithName("ListSessions")
            .WithSummary("Lists active parent sessions.")
            .Produces<IReadOnlyList<SessionSummary>>();

        auth.MapDelete("/sessions/{id}", async (string id, HttpContext context, ParentSessions sessions, CancellationToken ct) =>
        {
            if (!await sessions.RevokeAsync(id, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Session not found.");
            }

            if (context.User.FindFirstValue(Principals.SessionIdClaim) == id)
            {
                await context.SignOutAsync(AuthSchemes.Cookie);
            }

            return Results.NoContent();
        })
            .WithName("RevokeSession")
            .WithSummary("Revokes a parent session so its cookie stops working.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IResult CredentialsRequired() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A username and password are required.");
}

public sealed record Credentials(string? Username, string? Password);

public sealed record AuthStatusResponse(bool SetupRequired, string? PrincipalType, string? Name);

public sealed record AntiforgeryResponse(string Token, string HeaderName);
