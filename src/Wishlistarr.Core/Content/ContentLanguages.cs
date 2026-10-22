namespace Wishlistarr.Core.Content;

public static class ContentLanguages
{
    public static string? Canonical(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        string code = language.Trim().ToLowerInvariant();
        return code.Length == 2 && code.All(char.IsAsciiLetter) ? code : null;
    }
}
