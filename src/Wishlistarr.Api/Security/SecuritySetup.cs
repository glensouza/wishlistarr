using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Kids;
using Wishlistarr.Core.Kids;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Security;

public static class AuthSchemes
{
    public const string Default = "Wishlistarr";
    public const string Cookie = "ParentCookie";
    public const string KidCookie = "KidCookie";
    public const string ApiKey = "ApiKey";
    public const string TrustedNetwork = "TrustedNetwork";
}

public static class SecuritySetup
{
    public const string CookieName = "wishlistarr.auth";
    public const string KidCookieName = "wishlistarr.kid";
    public const string AntiforgeryCookieName = "wishlistarr.csrf";
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
    public const string LoginRateLimitPolicy = "login";

    public static IServiceCollection AddWishlistarrSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(options => options.IsValid(), "The Auth configuration section is invalid.")
            .Validate(
                options => options.HasTrustedHostsForBypass(),
                "Auth:TrustedNetworkBypass requires Auth:TrustedHosts to list the host names used to reach Wishlistarr.")
            .ValidateOnStart();

        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<SetupState>();
        services.AddSingleton<TrustedNetworks>();
        services.AddSingleton<IPasswordHasher<ParentAccount>, PasswordHasher<ParentAccount>>();
        services.AddScoped<ParentAccounts>();
        services.AddScoped<ParentSessions>();
        services.AddOptions<KidOptions>()
            .Bind(configuration.GetSection(KidOptions.SectionName))
            .Validate(options => options.IsValid(), "The Kids configuration section is invalid.")
            .ValidateOnStart();
        services.AddSingleton<IPasswordHasher<KidProfile>, PasswordHasher<KidProfile>>();
        services.AddSingleton<IParentNotifier, LoggingParentNotifier>();
        services.AddScoped<KidProfiles>();
        services.AddScoped<KidSignIn>();
        services.AddScoped<KidSessions>();

        services.AddAuthentication(AuthSchemes.Default)
            .AddPolicyScheme(AuthSchemes.Default, null, options => options.ForwardDefaultSelector = SelectScheme)
            .AddCookie(AuthSchemes.Cookie, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden);
                options.Events.OnValidatePrincipal = ValidateSessionAsync;
            })
            .AddCookie(AuthSchemes.KidCookie, options =>
            {
                options.Cookie.Name = KidCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden);
                options.Events.OnValidatePrincipal = KidEndpoints.ValidateKidSessionAsync;
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
            .AddScheme<AuthenticationSchemeOptions, TrustedNetworkAuthenticationHandler>(AuthSchemes.TrustedNetwork, null);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Parent, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(Principals.Parent)
                .RequireAssertion(context => !context.User.IsInRole(Principals.Kid)))
            .AddPolicy(Policies.Kid, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(Principals.Kid)
                .RequireAssertion(context => !context.User.IsInRole(Principals.Parent)));

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            options.AddPolicy(LoginRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginAttemptsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<AuthOptions>>((forwarded, auth) =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (string proxy in auth.Value.KnownProxies)
            {
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        });

        return services;
    }

    public static RouteGroupBuilder RequireParent(this RouteGroupBuilder group) =>
        group.RequireAuthorization(Policies.Parent);

    public static WebApplication UseKnownProxies(this WebApplication app)
    {
        // An empty known-proxy list makes the forwarded headers middleware trust every sender, so it is only added when proxies are configured.
        if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.KnownProxies.Count > 0)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }

    public static WebApplication UseWishlistarrSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(ValidateAntiforgeryAsync);
        app.Use(RequireSetupAsync);
        return app;
    }

    private static string SelectScheme(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey(ApiKeyService.HeaderName))
        {
            return AuthSchemes.ApiKey;
        }

        if (context.Request.Cookies.ContainsKey(CookieName))
        {
            return AuthSchemes.Cookie;
        }

        return context.Request.Cookies.ContainsKey(KidCookieName) ? AuthSchemes.KidCookie : AuthSchemes.TrustedNetwork;
    }

    private static Task SetStatus(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }

    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        string? sessionId = context.Principal?.FindFirstValue(Principals.SessionIdClaim);
        string? accountId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        ParentSessions sessions = context.HttpContext.RequestServices.GetRequiredService<ParentSessions>();
        if (sessionId is null
            || accountId is null
            || !await sessions.ValidateAsync(sessionId, accountId, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthSchemes.Cookie);
        }
    }

    private static async Task ValidateAntiforgeryAsync(HttpContext context, RequestDelegate next)
    {
        if (RequiresAntiforgery(context)
            && !await context.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid antiforgery token.",
                detail: $"Send the token from GET /api/v1/auth/antiforgery in the {AntiforgeryHeaderName} header.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool RequiresAntiforgery(HttpContext context) =>
        !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method)
        && !HttpMethods.IsTrace(context.Request.Method)
        && !context.Request.Path.StartsWithSegments("/api/v1/webhooks")
        && context.User.Identity is { IsAuthenticated: true, AuthenticationType: not AuthSchemes.ApiKey };

    private static async Task RequireSetupAsync(HttpContext context, RequestDelegate next)
    {
        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAuthorizeData>() is not null
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
            && !context.RequestServices.GetRequiredService<SetupState>().IsComplete)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Setup required.",
                detail: "Create the parent account with POST /api/v1/auth/setup first.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
