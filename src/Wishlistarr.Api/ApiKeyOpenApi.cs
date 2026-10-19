using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Wishlistarr.Api.Security;

namespace Wishlistarr.Api;

public static class ApiKeyOpenApi
{
    public const string SchemeName = "ApiKey";

    public static OpenApiOptions AddApiKeySecurity(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyService.HeaderName,
                Description = "API key generated on first start.",
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            if (context.Description.RelativePath?.StartsWith("api/v1", StringComparison.Ordinal) == true)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [],
                });
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
