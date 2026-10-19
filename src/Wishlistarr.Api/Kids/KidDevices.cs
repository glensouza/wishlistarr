using System.Security.Cryptography;
using System.Text;

namespace Wishlistarr.Api.Kids;

public static class KidDevices
{
    public const string CookieName = "wishlistarr.device";

    private const int IdBytes = 32;

    public static string Resolve(HttpContext context)
    {
        string? existing = context.Request.Cookies[CookieName];
        if (existing is null || existing.Length != Base64UrlLength || !existing.All(IsBase64Url))
        {
            existing = Convert.ToBase64String(RandomNumberGenerator.GetBytes(IdBytes))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            context.Response.Cookies.Append(CookieName, existing, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
            });
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(existing)));
    }

    private static int Base64UrlLength => (IdBytes * 4 / 3) + 1;

    private static bool IsBase64Url(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}
