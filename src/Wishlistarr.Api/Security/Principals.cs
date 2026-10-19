using System.Security.Claims;

namespace Wishlistarr.Api.Security;

public static class Principals
{
    public const string Parent = "Parent";
    public const string Kid = "Kid";
    public const string SessionIdClaim = "wishlistarr:session";

    public static ClaimsPrincipal CreateParent(string id, string name, string authenticationType, string? sessionId = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, id),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, Parent),
        ];
        if (sessionId is not null)
        {
            claims.Add(new Claim(SessionIdClaim, sessionId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    public static ClaimsPrincipal CreateKid(string id, string name, string authenticationType) =>
        new(new ClaimsIdentity(
            [
                new(ClaimTypes.NameIdentifier, id),
                new(ClaimTypes.Name, name),
                new(ClaimTypes.Role, Kid),
            ],
            authenticationType));

    public static string? GetPrincipalType(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        bool isParent = user.IsInRole(Parent);
        bool isKid = user.IsInRole(Kid);
        if (isParent == isKid)
        {
            return null;
        }

        return isParent ? Parent : Kid;
    }
}

public static class Policies
{
    public const string Parent = "Parent";
    public const string Kid = "Kid";
}
