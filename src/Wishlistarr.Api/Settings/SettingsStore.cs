using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Wishlistarr.Core.Settings;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Settings;

public sealed class SettingsStore(WishlistarrDbContext db, IDataProtectionProvider protectionProvider, TimeProvider time)
{
    public const string ProtectorPurpose = "Wishlistarr.Settings";
    public const string ReservedPrefix = "system.";

    private const int SaveAttempts = 5;

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

    public async Task<IReadOnlyDictionary<string, string?>> GetAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        Dictionary<string, string?> values = [];
        foreach (string key in keys)
        {
            values[key] = null;
        }

        if (values.Count == 0)
        {
            return values;
        }

        List<string> wanted = [.. values.Keys];
        List<Setting> settings = await db.Settings.AsNoTracking().Where(row => wanted.Contains(row.Key)).ToListAsync(ct);
        foreach (Setting setting in settings)
        {
            values[setting.Key] = setting.IsSecret ? this.protector.Unprotect(setting.Value) : setting.Value;
        }

        return values;
    }

    public Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default) =>
        this.SetAsync([new AppSettingChange(key, value, isSecret)], ct);

    public async Task SetAsync(IReadOnlyList<AppSettingChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await this.WriteAsync(changes, ct);
                return;
            }
            catch (DbUpdateException exception) when (IsConstraint(exception) && attempt < SaveAttempts - 1)
            {
                foreach (EntityEntry<Setting> entry in db.ChangeTracker.Entries<Setting>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await db.Settings.Where(row => row.Key == key).ExecuteDeleteAsync(ct);

    private static bool IsConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    private async Task WriteAsync(IReadOnlyList<AppSettingChange> changes, CancellationToken ct)
    {
        List<string> keys = [.. changes.Select(change => change.Key).Distinct()];
        List<Setting> existing = await db.Settings.Where(row => keys.Contains(row.Key)).ToListAsync(ct);
        Dictionary<string, Setting> byKey = existing.ToDictionary(row => row.Key);
        DateTimeOffset updatedAt = time.GetUtcNow();
        foreach (AppSettingChange change in changes)
        {
            string stored = change.IsSecret ? this.protector.Protect(change.Value) : change.Value;
            if (!byKey.TryGetValue(change.Key, out Setting? setting))
            {
                setting = new Setting
                {
                    Key = change.Key,
                    Value = stored,
                    IsSecret = change.IsSecret,
                    UpdatedAt = updatedAt,
                };
                db.Settings.Add(setting);
                byKey[change.Key] = setting;
            }
            else
            {
                setting.IsSecret = change.IsSecret;
                setting.Value = stored;
                setting.UpdatedAt = updatedAt;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed record SettingSummary(string Key, bool IsSecret, string? Value);
