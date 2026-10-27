using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Notifications;

public sealed record NotificationConfiguration
{
    public string Url { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string Targets { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;
    public bool EnableSsl { get; init; } = true;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
}

public sealed record NotificationUpdate(bool Enabled, string[] Events, NotificationConfiguration Configuration);

public sealed record NotificationSubscriptionView(string Provider, bool Enabled, string[] Events, NotificationConfiguration Configuration, bool HasSecret);

public sealed class NotificationSettings(WishlistarrDbContext db, IDataProtectionProvider protection, SettingsStore settings)
{
    public const string BaseUrlKey = "system.notifications.base_url";
    public static readonly string[] Providers = ["ntfy", "discord", "apprise", "smtp"];
    public static readonly string[] Events = ["pending", "approved", "denied", "available", "failed", "pinFailures", "profileLocked"];
    private readonly IDataProtector protector = protection.CreateProtector("Wishlistarr.Notifications.Configuration.v1");

    public async Task<int?> AccountAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (Principals.TryGetSessionAccount(user, out int id, out _))
        {
            return await db.ParentAccounts.AnyAsync(row => row.Id == id, ct) ? id : null;
        }

        // Machine credentials represent the sole owner only while there is exactly one parent.
        if (user.Identity?.AuthenticationType is AuthSchemes.ApiKey or AuthSchemes.TrustedNetwork)
        {
            List<int> owners = await db.ParentAccounts.Select(row => row.Id).Take(2).ToListAsync(ct);
            return owners.Count == 1 ? owners[0] : null;
        }

        return null;
    }

    public async Task<IReadOnlyList<NotificationSubscriptionView>> ListAsync(int parentId, CancellationToken ct)
    {
        List<NotificationSubscription> subscriptions = await db.NotificationSubscriptions.AsNoTracking()
            .Where(row => row.ParentAccountId == parentId).ToListAsync(ct);
        return Providers.Select(provider =>
        {
            NotificationSubscription? subscription = subscriptions.SingleOrDefault(row => row.Provider == provider);
            NotificationConfiguration config = subscription is null ? new() : this.Read(subscription);
            bool hasSecret = config.Url.Length > 0 || config.Token.Length > 0 || config.Targets.Length > 0 || config.Password.Length > 0;
            return new NotificationSubscriptionView(provider, subscription?.Enabled ?? false,
                subscription?.Events.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [],
                config with { Url = string.Empty, Token = string.Empty, Targets = string.Empty, Password = string.Empty }, hasSecret);
        }).ToList();
    }

    public NotificationConfiguration Read(NotificationSubscription subscription) =>
        JsonSerializer.Deserialize<NotificationConfiguration>(this.protector.Unprotect(subscription.ProtectedConfiguration))!;

    public Task<NotificationSubscription?> FindAsync(int parentId, string provider, CancellationToken ct) =>
        db.NotificationSubscriptions.FirstOrDefaultAsync(row => row.ParentAccountId == parentId && row.Provider == provider, ct);

    public async Task<string?> SaveAsync(int parentId, string provider, NotificationUpdate update, CancellationToken ct)
    {
        if (!Providers.Contains(provider) || update.Configuration is null || update.Events is null
            || update.Events.Length > Events.Length || update.Events.Any(item => !Events.Contains(item)))
        {
            return "Choose a supported provider and notification events.";
        }

        NotificationSubscription? subscription = await this.FindAsync(parentId, provider, ct);
        NotificationConfiguration previous = subscription is null ? new() : this.Read(subscription);
        NotificationConfiguration supplied = update.Configuration;
        NotificationConfiguration config = supplied with
        {
            Url = KeepSecret(supplied.Url, previous.Url),
            Token = KeepSecret(supplied.Token, previous.Token),
            Targets = KeepSecret(supplied.Targets, previous.Targets),
            Password = KeepSecret(supplied.Password, previous.Password),
        };
        if (provider == "apprise" && TryAppriseEndpoint(config.Url, out bool stateful) && stateful)
        {
            config = config with { Targets = string.Empty };
        }

        string? error = Validate(provider, config);
        if (error is not null)
        {
            return error;
        }

        if (update.Enabled && update.Events.Contains("pending") && string.IsNullOrEmpty(await this.BaseUrlAsync(ct)))
        {
            return "Save the public Wishlistarr URL before enabling approval notifications.";
        }

        if (subscription is null)
        {
            subscription = new NotificationSubscription
            {
                ParentAccountId = parentId,
                Provider = provider,
                ProtectedConfiguration = string.Empty,
                LastEventId = await db.MediaRequestEvents.MaxAsync(row => (int?)row.Id, ct) ?? 0,
            };
            db.NotificationSubscriptions.Add(subscription);
        }

        subscription.Enabled = update.Enabled;
        subscription.Events = string.Join(',', update.Events.Distinct());
        subscription.ProtectedConfiguration = this.protector.Protect(JsonSerializer.Serialize(config));
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string> BaseUrlAsync(CancellationToken ct) => await settings.GetAsync(BaseUrlKey, ct) ?? string.Empty;

    public async Task<bool> SaveBaseUrlAsync(string? url, CancellationToken ct)
    {
        if (!TryHttpUrl(url, out Uri? parsed) || url!.Length > 256 || parsed!.Query.Length > 0 || parsed.AbsolutePath != "/")
        {
            return false;
        }

        await settings.SetAsync(BaseUrlKey, parsed.GetLeftPart(UriPartial.Authority), false, ct);
        return true;
    }

    public static bool TryHttpUrl(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && value!.Length <= 2000
        && !value.Any(char.IsControl);

    public static bool TryAppriseEndpoint(string url, out bool stateful)
    {
        stateful = false;
        if (!TryHttpUrl(url, out Uri? uri))
        {
            return false;
        }

        string path = uri!.AbsolutePath;
        string[] segments = (path.EndsWith('/') ? path[..^1] : path).Split('/');
        int notify = Array.IndexOf(segments, "notify");
        stateful = notify >= 0 && segments.Length == notify + 2 && segments[^1].Length > 0;
        return stateful || (notify >= 0 && segments.Length == notify + 1);
    }

    private static string KeepSecret(string? supplied, string previous) => string.IsNullOrWhiteSpace(supplied) ? previous : supplied;

    private static string? Validate(string provider, NotificationConfiguration config)
    {
        if (JsonSerializer.Serialize(config).Length > 12000
            || new[] { config.Url, config.Token, config.Targets, config.Host, config.Username, config.Password, config.From, config.To }.Any(value => value is null || value.Any(char.IsControl)))
        {
            return "Notification configuration is invalid or too long.";
        }

        if (provider != "smtp")
        {
            if (!TryHttpUrl(config.Url, out Uri? url))
            {
                return "Use an absolute HTTP or HTTPS notification endpoint without embedded credentials or a fragment.";
            }

            if (provider == "ntfy" && (url!.AbsolutePath == "/" || url.Query.Length > 0))
            {
                return "The ntfy URL must include a topic and no query string.";
            }

            if (provider == "apprise" && !TryAppriseEndpoint(config.Url, out _))
            {
                return "Use an Apprise /notify or /notify/{key} endpoint, including any proxy path prefix.";
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(config.Host) || Uri.CheckHostName(config.Host) == UriHostNameType.Unknown
            || config.Port is < 1 or > 65535 || !MailAddress.TryCreate(config.From, out _) || !MailAddress.TryCreate(config.To, out _))
        {
            return "Set a valid SMTP host, port, sender and recipient address.";
        }

        if (config.Username.Length > 0 && !config.EnableSsl)
        {
            return "SMTP authentication requires STARTTLS.";
        }

        return null;
    }
}
