namespace Wishlistarr.Api.Security;

public sealed class ApiKeyEndpointFilter(ApiKeyService apiKeys) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        string? candidate = context.HttpContext.Request.Headers[ApiKeyService.HeaderName];
        if (!apiKeys.IsValid(candidate))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: $"A valid {ApiKeyService.HeaderName} header is required.");
        }

        return await next(context);
    }
}
