namespace Wishlistarr.Core.Kids;

public static class PlexLabelRules
{
    public const int MaxLength = 64;

    private const string ForbiddenCharacters = ",|";

    public static string? Validate(string label)
    {
        if (label.Length == 0 || label.Trim().Length != label.Length)
        {
            return "Plex labels cannot be empty or start or end with whitespace.";
        }

        if (label.Length > MaxLength)
        {
            return $"Plex labels are limited to {MaxLength} characters.";
        }

        if (label.Any(char.IsControl) || label.IndexOfAny(ForbiddenCharacters.ToCharArray()) >= 0)
        {
            return "Plex labels cannot contain commas, pipes or control characters.";
        }

        return null;
    }
}
