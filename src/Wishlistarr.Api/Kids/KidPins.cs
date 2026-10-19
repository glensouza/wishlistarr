namespace Wishlistarr.Api.Kids;

public static class KidPins
{
    public const int MinLength = 4;
    public const int MaxLength = 8;

    public static bool IsValid(string pin) =>
        pin.Length is >= MinLength and <= MaxLength && pin.All(char.IsAsciiDigit);
}
