using System.Globalization;
using System.Text.Json;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Api.Updates;

public sealed record PublishedRelease(string Tag, bool Draft, bool Prerelease);

public sealed record ChosenRelease(string Tag, SemanticVersion Version);

public static class ReleaseSelection
{
    public static ChosenRelease? Choose(SemanticVersion current, IEnumerable<PublishedRelease> releases)
    {
        ChosenRelease? best = null;
        foreach (PublishedRelease release in releases)
        {
            if (release.Draft || !SemanticVersion.TryParseTag(release.Tag, out SemanticVersion? version) || version is null)
            {
                continue;
            }

            bool prerelease = release.Prerelease || version.IsPrerelease;
            if (prerelease && !current.IsPrerelease)
            {
                continue;
            }

            if (version.CompareTo(current) <= 0)
            {
                continue;
            }

            if (best is null || version.CompareTo(best.Version) > 0)
            {
                best = new ChosenRelease(release.Tag, version);
            }
        }

        return best;
    }
}

public sealed class UpdateChecker(IHttpClientFactory httpClients, SettingsStore settings, TimeProvider time, ILogger<UpdateChecker> logger)
{
    public const string HttpClientName = "Wishlistarr.Updates";

    public const string CheckSettingKey = "updates.check";

    public const string CheckedAtKey = "system.updates.checked_at";

    public const string TagKey = "system.updates.tag";

    public const string ReleaseNotesUrlKey = "system.updates.release_notes_url";

    public const string ReleasesUrl = "https://api.github.com/repos/glensouza/wishlistarr/releases?per_page=100";

    public const string UpgradeNotesUrl = "https://github.com/glensouza/wishlistarr/blob/main/docs/upgrading.md";

    public const string AcceptMediaType = "application/vnd.github+json";

    public const string ApiVersion = "2022-11-28";

    public const int MaxResponseBytes = 5 * 1024 * 1024;

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static string UserAgent => "Wishlistarr/" + AppVersion.Current;

    public static string ReleaseNotesUrlFor(string tag) =>
        "https://github.com/glensouza/wishlistarr/releases/tag/" + Uri.EscapeDataString(tag);

    public static bool IsCheckEnabled(string? value) =>
        value is null || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseCheckEnabled(string? value, out bool enabled)
    {
        if (value is not null && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        if (value is not null && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }

        enabled = false;
        return false;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await this.CheckAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Update check failed.");
        }
    }

    public async Task<UpdateNoticeResponse> GetAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string?> values = await settings.GetAsync([CheckSettingKey, TagKey, ReleaseNotesUrlKey], ct);
        if (!IsCheckEnabled(values[CheckSettingKey]))
        {
            return new UpdateNoticeResponse(false, null);
        }

        AvailableUpdate? available = Describe(values[TagKey], values[ReleaseNotesUrlKey]);
        return new UpdateNoticeResponse(true, available);
    }

    public static AvailableUpdate? Describe(string? tag, string? url)
    {
        if (tag is null
            || url is null
            || !SemanticVersion.TryParse(AppVersion.Current, out SemanticVersion? current)
            || current is null
            || !SemanticVersion.TryParseTag(tag, out SemanticVersion? published)
            || published is null
            || published.CompareTo(current) <= 0
            || (!current.IsPrerelease && published.IsPrerelease)
            || !string.Equals(url, ReleaseNotesUrlFor(tag), StringComparison.Ordinal))
        {
            return null;
        }

        return new AvailableUpdate(published.ToString(), url, UpgradeNotesUrl);
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (!IsCheckEnabled(await settings.GetAsync(CheckSettingKey, ct))
            || await this.CheckedRecentlyAsync(ct))
        {
            return;
        }

        if (!SemanticVersion.TryParse(AppVersion.Current, out SemanticVersion? current) || current is null)
        {
            logger.LogWarning("Update check failed because the running version is not a semantic version.");
            return;
        }

        await settings.SetAsync(CheckedAtKey, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), false, ct);
        IReadOnlyList<PublishedRelease>? releases = await this.FetchAsync(ct);
        if (releases is null)
        {
            return;
        }

        ChosenRelease? chosen = ReleaseSelection.Choose(current, releases);
        if (chosen is null)
        {
            await settings.DeleteAsync(TagKey, ct);
            await settings.DeleteAsync(ReleaseNotesUrlKey, ct);
            return;
        }

        await settings.SetAsync(
            [
                new AppSettingChange(TagKey, chosen.Tag, false),
                new AppSettingChange(ReleaseNotesUrlKey, ReleaseNotesUrlFor(chosen.Tag), false),
            ],
            ct);
    }

    private async Task<bool> CheckedRecentlyAsync(CancellationToken ct)
    {
        string? stored = await settings.GetAsync(CheckedAtKey, ct);
        if (!DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset checkedAt))
        {
            return false;
        }

        return time.GetUtcNow() - checkedAt < Interval;
    }

    private async Task<IReadOnlyList<PublishedRelease>?> FetchAsync(CancellationToken ct)
    {
        using HttpClient http = httpClients.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Get, ReleasesUrl);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Update check failed with HTTP status {Status}.", (int)response.StatusCode);
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            return ParseReleases(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Update check failed.");
            return null;
        }
        catch (JsonException)
        {
            logger.LogWarning("Update check failed because the response could not be read.");
            return null;
        }
    }

    private static List<PublishedRelease> ParseReleases(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Release list was not an array.");
        }

        List<PublishedRelease> releases = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (TryReadRelease(item, out PublishedRelease? release) && release is not null)
            {
                releases.Add(release);
            }
        }

        return releases;
    }

    private static bool TryReadRelease(JsonElement item, out PublishedRelease? release)
    {
        release = null;
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("tag_name", out JsonElement tag)
            || tag.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("draft", out JsonElement draft)
            || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !item.TryGetProperty("prerelease", out JsonElement prerelease)
            || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        string? tagName = tag.GetString();
        if (tagName is null)
        {
            return false;
        }

        release = new PublishedRelease(tagName, draft.GetBoolean(), prerelease.GetBoolean());
        return true;
    }
}

public sealed class UpdateCheckJob(UpdateChecker checker) : IBackgroundJob
{
    public const string JobName = "update-check";

    public Task RunAsync(CancellationToken ct) => checker.RunAsync(ct);
}
