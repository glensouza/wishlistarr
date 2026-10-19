using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Wishlistarr.Api.Security;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService apiKeys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!this.Request.Headers.TryGetValue(ApiKeyService.HeaderName, out StringValues values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (values.Count != 1 || !apiKeys.IsValid(values[0]))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Invalid {ApiKeyService.HeaderName} header."));
        }

        AuthenticationTicket ticket = new(
            Principals.CreateParent("api-key", "API key", this.Scheme.Name),
            this.Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
