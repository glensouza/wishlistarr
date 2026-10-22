using Microsoft.AspNetCore.Authorization;

namespace Wishlistarr.Api.Kids;

public sealed class PairedDeviceRequirement : IAuthorizationRequirement;

public sealed class PairedDeviceHandler : AuthorizationHandler<PairedDeviceRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PairedDeviceRequirement requirement)
    {
        if (context.Resource is HttpContext http
            && await http.RequestServices.GetRequiredService<KidDevices>().FindCurrentAsync(http, http.RequestAborted) is not null)
        {
            context.Succeed(requirement);
        }
    }
}
