namespace Wishlistarr.Api.Requests;

public static class PlexWatchLink
{
    public static bool IsMachineId(string? value) => IsToken(value, 100);

    public static bool IsRatingKey(string? value) => IsToken(value, 20);

    public static KidPlexLinks? TryCreate(string? machineId, string? ratingKey)
    {
        if (!IsMachineId(machineId) || !IsRatingKey(ratingKey) || machineId is null || ratingKey is null)
        {
            return null;
        }

        return new KidPlexLinks("https://app.plex.tv/desktop/#!/server/" + machineId + "/details?key=%2Flibrary%2Fmetadata%2F" + ratingKey);
    }

    private static bool IsToken(string? value, int max) =>
        value is { Length: > 0 } && value.Length <= max && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
