using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Security;

namespace Wishlistarr.Tests;

public class AuthApiFactory : ApiFactory
{
    public AuthApiFactory()
    {
    }

    protected AuthApiFactory(string dataPath, bool deleteData)
        : base(dataPath, deleteData)
    {
    }

    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const string KidHeader = "X-Test-Kid";
    public const string KidScheme = "TestKid";

    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>
    {
        ["Auth:LoginAttemptsPerMinute"] = "1000",
    };

    public async Task<HttpClient> CreateSignedInClientAsync()
    {
        await this.EnsureParentAsync();
        HttpClient client = this.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ParentUsername, password = ParentPassword });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AddAntiforgeryTokenAsync(client);
        return client;
    }

    public async Task<HttpClient> PairDeviceAsync(HttpClient parent, string name, IReadOnlyList<int> profileIds)
    {
        HttpResponseMessage created = await parent.PostAsJsonAsync("/api/v1/kid-devices", new { name, profileIds });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string code = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        HttpClient device = this.CreateClient();
        HttpResponseMessage paired = await device.PostAsJsonAsync("/api/v1/kid/pair", new { code });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        return device;
    }

    public static async Task AddAntiforgeryTokenAsync(HttpClient client)
    {
        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        string header = body.GetProperty("headerName").GetString()!;
        client.DefaultRequestHeaders.Remove(header);
        client.DefaultRequestHeaders.Add(header, body.GetProperty("token").GetString());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(this.Settings));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, KidAuthenticationHandler>(KidScheme, null);
            services.PostConfigure<PolicySchemeOptions>(AuthSchemes.Default, options =>
            {
                Func<HttpContext, string?>? selector = options.ForwardDefaultSelector;
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(KidHeader) ? KidScheme : selector?.Invoke(context);
            });
        });
    }

    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[RemoteIpHeader], out IPAddress? address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class KidAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                Principals.CreateKid("kid-1", "Test kid", this.Scheme.Name),
                this.Scheme.Name)));
    }
}
