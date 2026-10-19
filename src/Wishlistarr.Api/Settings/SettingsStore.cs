using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Settings;

public sealed class SettingsStore(WishlistarrDbContext db, IDataProtectionProvider protectionProvider, TimeProvider time)
{
    public const string ProtectorPurpose = "Wishlistarr.Settings";
    public const string ReservedPrefix = "system.";

    private readonly IDataProtector protector = protectionProvider.CreateProtector(ProtectorPurpose);

    public static bool IsReserved(string key) => key.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SettingSummary>> ListAsync(CancellationToken ct = default)
    {
        List<Setting> settings = await db.Settings.AsNoTracking().Where(setting => !setting.Key.StartsWith(ReservedPrefix)).OrderBy(setting => setting.Key).ToListAsync(ct);
        return settings
            .Select(setting => new SettingSummary(setting.Key, setting.IsSecret, setting.IsSecret ? null : setting.Value))
            .ToList();
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        Setting? setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(row => row.Key == key, ct);
        if (setting is null)
        {
            return null;
        }

        return setting.IsSecret ? this.protector.Unprotect(setting.Value) : setting.Value;
    }

    public async Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default)
    {
        Setting? setting = await db.Settings.FirstOrDefaultAsync(row => row.Key == key, ct);
        if (setting is null)
        {
            setting = new Setting { Key = key, Value = string.Empty };
            db.Settings.Add(setting);
        }

        setting.IsSecret = isSecret;
        setting.Value = isSecret ? this.protector.Protect(value) : value;
        setting.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await db.Settings.Where(row => row.Key == key).ExecuteDeleteAsync(ct);
}

public sealed record SettingSummary(string Key, bool IsSecret, string? Value);
