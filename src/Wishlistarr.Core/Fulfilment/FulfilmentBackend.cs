namespace Wishlistarr.Core.Fulfilment;

public enum FulfilmentBackend
{
    Seerr,
    Arr,
}

public static class FulfilmentModes
{
    public const string SettingKey = "fulfilment.mode";

    public static bool TryParse(string? value, out FulfilmentBackend? backend)
    {
        backend = null;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "seerr":
                backend = FulfilmentBackend.Seerr;
                return true;
            case "arr":
                backend = FulfilmentBackend.Arr;
                return true;
            case "none":
                return true;
            default:
                return false;
        }
    }
}
