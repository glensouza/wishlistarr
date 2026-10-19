using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Security;

namespace Wishlistarr.Tests;

public sealed partial class AuthorizationBoundaryTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly HashSet<string> PublicApiRoutes =
    [
        "/api/health",
        "/api/v1/auth/status",
        "/api/v1/auth/antiforgery",
        "/api/v1/auth/setup",
        "/api/v1/auth/login",
        "/api/{**path}",
    ];

    [Fact]
    public async Task Kid_principal_is_rejected_on_every_parent_route()
    {
        await factory.EnsureParentAsync();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        List<(string Method, string Path)> routes = [.. this.GetParentRoutes()];
        List<string> leaks = [];
        foreach ((string method, string path) in routes)
        {
            HttpResponseMessage asKid = await kid.SendAsync(CreateRequest(method, path));
            if (asKid.StatusCode != HttpStatusCode.Forbidden)
            {
                leaks.Add($"{method} {path} returned {(int)asKid.StatusCode} for a kid");
            }

            HttpResponseMessage asParent = await parent.SendAsync(CreateRequest(method, path));
            if (asParent.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                leaks.Add($"{method} {path} returned {(int)asParent.StatusCode} for a parent");
            }
        }

        Assert.Contains(("GET", "/api/v1/system/status"), routes);
        Assert.Contains(("DELETE", "/api/v1/auth/sessions/x"), routes);
        Assert.Empty(leaks);
    }

    [Fact]
    public void Every_api_endpoint_is_either_public_or_parent_only()
    {
        List<string> unprotected = [];
        foreach (RouteEndpoint endpoint in this.GetRouteEndpoints())
        {
            string pattern = Pattern(endpoint);
            if (pattern.StartsWith("/api", StringComparison.Ordinal)
                && !PublicApiRoutes.Contains(pattern)
                && !IsParentOnly(endpoint))
            {
                unprotected.Add(pattern);
            }
        }

        Assert.Empty(unprotected);
    }

    [Fact]
    public async Task Parent_and_kid_policies_are_mutually_exclusive()
    {
        IAuthorizationService authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        ClaimsPrincipal kid = Principals.CreateKid("kid-1", "Kid", "Test");
        ClaimsPrincipal parent = Principals.CreateParent("1", "Parent", "Test");
        ClaimsPrincipal both = Principals.CreateParent("1", "Parent", "Test");
        both.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, Principals.Kid)], "Test"));
        ClaimsPrincipal anonymous = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, Principals.Parent)]));

        Assert.False((await authorization.AuthorizeAsync(kid, Policies.Parent)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(kid, Policies.Kid)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(parent, Policies.Parent)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(parent, Policies.Kid)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(both, Policies.Parent)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(both, Policies.Kid)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(anonymous, Policies.Parent)).Succeeded);
    }

    private IEnumerable<(string Method, string Path)> GetParentRoutes()
    {
        foreach (RouteEndpoint endpoint in this.GetRouteEndpoints().Where(IsParentOnly))
        {
            string path = RouteParameter().Replace(Pattern(endpoint), "x");
            IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (string method in methods)
            {
                yield return (method, path);
            }
        }
    }

    private IEnumerable<RouteEndpoint> GetRouteEndpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private static bool IsParentOnly(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == Policies.Parent)
        && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;

    private static string Pattern(RouteEndpoint endpoint) => "/" + endpoint.RoutePattern.RawText?.TrimStart('/');

    private static HttpRequestMessage CreateRequest(string method, string path)
    {
        HttpRequestMessage request = new(new HttpMethod(method), path);
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsDelete(method))
        {
            request.Content = JsonContent.Create(new { });
        }

        return request;
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();
}
