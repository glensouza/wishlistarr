using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Search;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Titles;

public enum KidTitleOutcome
{
    Ok,
    NotFound,
    ProfileNotFound,
}

public sealed record KidCastMember(string Name, string? Character, string? ProfileUrl);

public sealed record KidSeason(int Number, string? Name, string? Overview, string? PosterUrl, int EpisodeCount, string? Year);

public sealed record KidTitleRequest(RequestState State, int RequestCount);

public sealed record KidTitleResponse(
    MediaKind Kind,
    int TmdbId,
    string Title,
    string? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    int? RuntimeMinutes,
    string? Rating,
    Availability? Availability,
    bool NeedsParent,
    string? TrailerKey,
    IReadOnlyList<KidCastMember> Cast,
    IReadOnlyList<KidSeason> Seasons,
    KidTitleRequest? Request);

public sealed record KidTitleResult(KidTitleOutcome Outcome, KidTitleResponse? Title = null);

public sealed partial class KidTitleDetails(
    WishlistarrDbContext db,
    ITmdbClient tmdb,
    IEnumerable<ILibraryIndex> indexes,
    SettingsStore settings,
    ILogger<KidTitleDetails> logger)
{
    public const int MaxCast = 12;
    public const int MaxOverviewLength = 2000;

    public async Task<KidTitleResult> GetAsync(int kidProfileId, MediaKind kind, int tmdbId, CancellationToken ct = default)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == kidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return new KidTitleResult(KidTitleOutcome.ProfileNotFound);
        }

        TmdbMediaType type = kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        TmdbDetails details;
        try
        {
            details = await tmdb.GetDetailsAsync(type, tmdbId, ct: ct);
        }
        catch (TmdbException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new KidTitleResult(KidTitleOutcome.NotFound);
        }

        string title = (kind == MediaKind.Movie ? details.Title : details.Name)?.Trim() ?? string.Empty;
        if (title.Length == 0 || details.Adult)
        {
            if (details.Adult)
            {
                this.LogAdult(kind, tmdbId);
            }

            return new KidTitleResult(KidTitleOutcome.NotFound);
        }

        TitleKey key = new(kind, tmdbId);
        TmdbCertification? certification;
        try
        {
            certification = await tmdb.GetCertificationAsync(type, tmdbId, [RatingScale.UsRegion], ct);
        }
        catch (Exception ex) when (LookupFailed(ex, ct))
        {
            this.LogCertificationFailed(ex, kind, tmdbId);
            return new KidTitleResult(KidTitleOutcome.NotFound);
        }

        ContentPolicy policy = ContentPolicy.ForProfile(
            profile.MovieAgeLimit,
            profile.TvAgeLimit,
            profile.BlockedGenreIds,
            profile.BlockedKeywordIds,
            profile.AllowedOriginalLanguages);
        IReadOnlyList<int>? keywordIds = null;
        if (policy.BlockedKeywordIds.Count > 0)
        {
            try
            {
                keywordIds = [.. (await tmdb.GetKeywordsAsync(type, tmdbId, ct)).Select(item => item.Id)];
            }
            catch (Exception ex) when (LookupFailed(ex, ct))
            {
                this.LogKeywordFailed(ex, kind, tmdbId);
                return new KidTitleResult(KidTitleOutcome.NotFound);
            }
        }

        ContentDecision decision = ContentPolicyEngine.Default.Evaluate(
            new ContentTitle(
                key,
                certification is null ? [] : [new RegionCertification(certification.Region, certification.Rating)],
                details.Genres is null ? null : [.. details.Genres.Select(genre => genre.Id)],
                keywordIds,
                details.OriginalLanguage),
            policy);
        if (decision.Outcome == ContentOutcome.Blocked)
        {
            this.LogHidden(kind, tmdbId, profile.Id, decision.Reason.Code);
            return new KidTitleResult(KidTitleOutcome.NotFound);
        }

        bool masked = decision.Outcome == ContentOutcome.NeedsApproval;
        bool trailers = !masked && YoutubeTrailers.IsEnabled(await settings.GetAsync(YoutubeTrailers.SettingKey, ct));
        IReadOnlyList<KidCastMember> cast = masked ? [] : await this.CastAsync(type, tmdbId, kind, ct);
        string? trailer = trailers ? await this.TrailerAsync(type, tmdbId, kind, ct) : null;
        string? rating = certification?.Rating?.Trim();
        if (string.IsNullOrEmpty(rating))
        {
            rating = null;
        }
        MediaRequest? open = await db.MediaRequests.AsNoTracking()
            .Where(request => request.KidProfileId == profile.Id
                && request.Kind == kind
                && request.TmdbId == tmdbId
                && request.State != RequestState.Denied)
            .OrderByDescending(request => request.Id)
            .FirstOrDefaultAsync(ct);

        return new KidTitleResult(
            KidTitleOutcome.Ok,
            new KidTitleResponse(
                kind,
                tmdbId,
                title,
                Year(kind == MediaKind.Movie ? details.ReleaseDate : details.FirstAirDate),
                masked ? null : Synopsis(details.Overview),
                masked ? null : ImageUrl(KidContentPipeline.PosterSize, details.PosterPath),
                masked ? null : ImageUrl(KidSearch.BackdropSize, details.BackdropPath),
                Runtime(kind, details),
                rating,
                await this.AvailabilityAsync(key, ct),
                masked,
                trailer,
                cast,
                Seasons(details, masked),
                open is null ? null : new KidTitleRequest(open.State, open.RequestCount)));
    }

    private async Task<IReadOnlyList<KidCastMember>> CastAsync(TmdbMediaType type, int tmdbId, MediaKind kind, CancellationToken ct)
    {
        TmdbCredits credits;
        try
        {
            credits = await tmdb.GetCreditsAsync(type, tmdbId, ct: ct);
        }
        catch (Exception ex) when (LookupFailed(ex, ct))
        {
            this.LogCreditsFailed(ex, kind, tmdbId);
            return [];
        }

        List<KidCastMember> cast = [];
        foreach (TmdbCastMember member in credits.Cast)
        {
            string name = member.Name.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            string? character = member.Character?.Trim();
            cast.Add(new KidCastMember(
                name,
                string.IsNullOrEmpty(character) ? null : character,
                ImageUrl(KidSearch.ProfileSize, member.ProfilePath)));
            if (cast.Count == MaxCast)
            {
                break;
            }
        }

        return cast;
    }

    private async Task<string?> TrailerAsync(TmdbMediaType type, int tmdbId, MediaKind kind, CancellationToken ct)
    {
        TmdbVideos videos;
        try
        {
            videos = await tmdb.GetVideosAsync(type, tmdbId, ct: ct);
        }
        catch (Exception ex) when (LookupFailed(ex, ct))
        {
            this.LogVideosFailed(ex, kind, tmdbId);
            return null;
        }

        return SelectTrailer(videos.Results);
    }

    internal static string? SelectTrailer(IReadOnlyList<TmdbVideo>? videos)
    {
        if (videos is null)
        {
            return null;
        }

        string? fallback = null;
        foreach (TmdbVideo video in videos)
        {
            if (video.Site != "YouTube" || video.Type != "Trailer" || !IsYoutubeId(video.Key))
            {
                continue;
            }

            if (video.Official)
            {
                return video.Key;
            }

            fallback ??= video.Key;
        }

        return fallback;
    }

    internal static bool IsYoutubeId(string? key) => key is not null && YoutubeId().IsMatch(key);

    private async Task<Availability?> AvailabilityAsync(TitleKey title, CancellationToken ct)
    {
        ExternalId id = new(ExternalIdSource.Tmdb, title.TmdbId.ToString(CultureInfo.InvariantCulture));
        foreach (ILibraryIndex index in indexes)
        {
            if (await index.FindAsync(title.Kind, id, ct) is IReadOnlyList<MediaItem> items)
            {
                return items.Any(item => item.Kind == title.Kind) ? Availability.InLibrary : Availability.NotInLibrary;
            }
        }

        return null;
    }

    private static IReadOnlyList<KidSeason> Seasons(TmdbDetails details, bool masked)
    {
        if (details.Seasons is null)
        {
            return [];
        }

        List<KidSeason> seasons = [];
        HashSet<int> seen = [];
        foreach (TmdbSeason season in details.Seasons.OrderBy(item => item.SeasonNumber))
        {
            if (season.SeasonNumber < 0 || !seen.Add(season.SeasonNumber))
            {
                continue;
            }

            string? name = season.Name?.Trim();
            seasons.Add(new KidSeason(
                season.SeasonNumber,
                masked || string.IsNullOrEmpty(name) ? null : name,
                masked ? null : Synopsis(season.Overview),
                masked ? null : ImageUrl(KidSearch.ProfileSize, season.PosterPath),
                Math.Max(0, season.EpisodeCount),
                Year(season.AirDate)));
        }

        return seasons;
    }

    private static int? Runtime(MediaKind kind, TmdbDetails details)
    {
        if (kind == MediaKind.Movie)
        {
            return details.Runtime is int minutes && minutes > 0 ? minutes : null;
        }

        foreach (int minutes in details.EpisodeRunTime ?? [])
        {
            if (minutes > 0)
            {
                return minutes;
            }
        }

        return null;
    }

    private static string? Synopsis(string? overview)
    {
        string text = overview?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= MaxOverviewLength ? text : text[..MaxOverviewLength];
    }

    private static string? Year(string? date) =>
        date is { Length: >= 4 } && date[..4].All(char.IsAsciiDigit) ? date[..4] : null;

    private static string? ImageUrl(string size, string? path)
    {
        string file = (path ?? string.Empty).TrimStart('/');
        return KidSearch.IsImageSize(size) && TmdbImageProxy.IsAllowed(size, file)
            ? $"{KidContentPipeline.PosterRoute}/{size}/{file}"
            : null;
    }

    private static bool LookupFailed(Exception ex, CancellationToken ct) =>
        ex is not TmdbNotConfiguredException
        && !ct.IsCancellationRequested
        && ex is TmdbException or HttpRequestException or TaskCanceledException;

    [LoggerMessage(Level = LogLevel.Information, Message = "Title {Kind} {TmdbId} was not shown to profile {ProfileId} ({Reason})")]
    private partial void LogHidden(MediaKind kind, int tmdbId, int profileId, ContentReasonCode reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Adult title {Kind} {TmdbId} was not shown")]
    private partial void LogAdult(MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certification lookup failed for {Kind} {TmdbId}; the title was not shown")]
    private partial void LogCertificationFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keyword lookup failed for {Kind} {TmdbId}; the title was not shown")]
    private partial void LogKeywordFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Credits lookup failed for {Kind} {TmdbId}; cast was left out")]
    private partial void LogCreditsFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Video lookup failed for {Kind} {TmdbId}; the trailer was left out")]
    private partial void LogVideosFailed(Exception exception, MediaKind kind, int tmdbId);

    [GeneratedRegex("^[A-Za-z0-9_-]{11}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex YoutubeId();
}
