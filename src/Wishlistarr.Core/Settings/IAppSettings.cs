namespace Wishlistarr.Core.Settings;

public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
