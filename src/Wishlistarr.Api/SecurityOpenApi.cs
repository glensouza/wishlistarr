using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Security;

namespace Wishlistarr.Api;

public static class SecurityOpenApi
{
    public const string ApiKeySchemeName = "ApiKey";
    public const string SessionSchemeName = "ParentSession";
    public const string KidDeviceSchemeName = "KidDevice";

    public static OpenApiOptions AddSecuritySchemes(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[ApiKeySchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyService.HeaderName,
                Description = "API key generated on first start.",
            };
            document.Components.SecuritySchemes[SessionSchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = SecuritySetup.CookieName,
                Description = $"Parent session cookie. Writes also need the {SecuritySetup.AntiforgeryHeaderName} header.",
            };
            document.Components.SecuritySchemes[KidDeviceSchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = KidDevices.CookieName,
                Description = "Device cookie set when a browser is paired as a kid device.",
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAuthorizeData>().Any(data => data.Policy == Policies.Parent)
                && !metadata.OfType<IAllowAnonymous>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(ApiKeySchemeName, context.Document)] = [],
                });
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SessionSchemeName, context.Document)] = [],
                });
            }

            if (metadata.OfType<IAuthorizeData>().Any(data => data.Policy == Policies.KidDevice)
                && !metadata.OfType<IAllowAnonymous>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(KidDeviceSchemeName, context.Document)] = [],
                });
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
