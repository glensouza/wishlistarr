namespace Wishlistarr.Core.Settings;

public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string?>> GetAsync(IReadOnlyList<string> keys, CancellationToken ct = default);

    Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default);

    Task SetAsync(IReadOnlyList<AppSettingChange> changes, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed record AppSettingChange(string Key, string Value, bool IsSecret);
