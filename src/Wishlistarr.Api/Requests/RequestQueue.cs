using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.Titles;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public enum QueueGap
{
    Synopsis,
    Rating,
    Providers,
    Trailer,
}

public enum QueueReasonCode
{
    Unrated,
    UnknownGenres,
    RevokedBefore,
    AboveCeiling,
    AutoApproveOff,
    NotAutoApproved,
    AboveRatingLimit,
    BlockedGenre,
    BlockedKeyword,
    LanguageNotAllowed,
    ExplicitlyBlocked,
    Unavailable,
}

public sealed record QueueWatchProvider(string Name);

public sealed record QueueReason(
    QueueReasonCode Code,
    string? Rating,
    RatingLevel? Level,
    RatingLevel? MaxLevel,
    string? GenreName,
    string? KeywordName,
    string? Language);

public sealed record ApprovalQueueItem(
    int Id,
    int KidProfileId,
    string KidName,
    MediaKind Kind,
    int TmdbId,
    string Title,
    int RequestCount,
    DateTimeOffset CreatedAt,
    string? Overview,
    string? Rating,
    IReadOnlyList<string> Descriptors,
    IReadOnlyList<QueueWatchProvider> Providers,
    string? TrailerKey,
    QueueReason Reason,
    IReadOnlyList<QueueGap> Gaps);

public sealed record ApprovalQueuePage(IReadOnlyList<ApprovalQueueItem> Items, int Total, int Limit, int Offset);

public sealed partial class RequestQueue(
    WishlistarrDbContext db,
    ITmdbClient tmdb,
    RequestTitles titles,
    SettingsStore settings,
    ILogger<RequestQueue> logger)
{
    public async Task<ApprovalQueuePage?> GetAsync(int? kidProfileId, int limit, int offset, CancellationToken ct)
    {
        if (kidProfileId is int profileId
            && !await db.KidProfiles.AsNoTracking().AnyAsync(profile => profile.Id == profileId, ct))
        {
            return null;
        }

        IQueryable<QueuedRequest> matching = db.MediaRequests.AsNoTracking()
            .Where(request => request.State == RequestState.Pending)
            .Where(request => kidProfileId == null || request.KidProfileId == kidProfileId)
            .Join(
                db.KidProfiles.AsNoTracking(),
                request => request.KidProfileId,
                profile => profile.Id,
                (request, profile) => new QueuedRequest { Request = request, Profile = profile });

        int total;
        List<QueuedRequest> listed;
        await db.Database.OpenConnectionAsync(ct);
        await using (SqliteTransaction snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true))
        await using (await db.Database.UseTransactionAsync(snapshot, ct))
        {
            total = await matching.CountAsync(ct);
            listed = await matching
                .OrderBy(item => item.Request.Id)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(ct);
        }

        if (listed.Count == 0)
        {
            return new ApprovalQueuePage([], total, limit, offset);
        }

        IReadOnlyDictionary<int, string> names = await titles.GetManyAsync([.. listed.Select(item => item.Request)], ct);
        HashSet<(int ProfileId, MediaKind Kind, int TmdbId)> revoked = await this.RevokedAsync(listed, ct);
        bool trailers = YoutubeTrailers.IsEnabled(await settings.GetAsync(YoutubeTrailers.SettingKey, ct));
        List<ApprovalQueueItem> items = [];
        foreach (QueuedRequest item in listed)
        {
            items.Add(await this.DescribeAsync(item, names[item.Request.Id], revoked, trailers, ct));
        }

        return new ApprovalQueuePage(items, total, limit, offset);
    }

    internal static IReadOnlyList<string> StreamingNames(TmdbWatchProviders? providers)
    {
        if (providers?.Results is null)
        {
            return [];
        }

        TmdbRegionProviders? region = null;
        foreach ((string code, TmdbRegionProviders value) in providers.Results)
        {
            if (string.Equals(code, RatingScale.UsRegion, StringComparison.OrdinalIgnoreCase))
            {
                region = value;
                break;
            }
        }

        if (region?.Flatrate is null)
        {
            return [];
        }

        List<string> names = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (TmdbProvider provider in region.Flatrate)
        {
            string name = provider.ProviderName.Trim();
            if (name.Length > 0 && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private async Task<HashSet<(int ProfileId, MediaKind Kind, int TmdbId)>> RevokedAsync(
        IReadOnlyList<QueuedRequest> listed,
        CancellationToken ct)
    {
        int[] profileIds = [.. listed.Select(item => item.Profile.Id).Distinct()];
        var rows = await db.MediaRequests.AsNoTracking()
            .Where(request => request.State == RequestState.Revoked && profileIds.Contains(request.KidProfileId))
            .Select(request => new { request.KidProfileId, request.Kind, request.TmdbId })
            .ToListAsync(ct);
        return [.. rows.Select(row => (row.KidProfileId, row.Kind, row.TmdbId))];
    }

    private async Task<ApprovalQueueItem> DescribeAsync(
        QueuedRequest item,
        string title,
        HashSet<(int ProfileId, MediaKind Kind, int TmdbId)> revoked,
        bool trailers,
        CancellationToken ct)
    {
        MediaRequest request = item.Request;
        KidProfile profile = item.Profile;
        TmdbMediaType type = request.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        List<QueueGap> gaps = [];
        TmdbDetails? details = null;
        try
        {
            details = await tmdb.GetDetailsAsync(type, request.TmdbId, ct: ct);
        }
        catch (Exception ex) when (LookupFailed(ex, ct))
        {
            this.LogDetailsFailed(ex, request.Kind, request.TmdbId);
            gaps.Add(QueueGap.Synopsis);
            gaps.Add(QueueGap.Rating);
            gaps.Add(QueueGap.Providers);
            if (trailers)
            {
                gaps.Add(QueueGap.Trailer);
            }
        }

        TmdbCertification? certification = null;
        if (details is not null)
        {
            try
            {
                certification = await tmdb.GetCertificationAsync(type, request.TmdbId, [RatingScale.UsRegion], ct);
            }
            catch (Exception ex) when (LookupFailed(ex, ct))
            {
                this.LogCertificationFailed(ex, request.Kind, request.TmdbId);
                gaps.Add(QueueGap.Rating);
            }
        }

        IReadOnlyList<QueueWatchProvider> providers = [];
        if (details is not null)
        {
            try
            {
                providers = [.. StreamingNames(await tmdb.GetWatchProvidersAsync(type, request.TmdbId, ct))
                    .Select(name => new QueueWatchProvider(name))];
            }
            catch (Exception ex) when (LookupFailed(ex, ct))
            {
                this.LogProvidersFailed(ex, request.Kind, request.TmdbId);
                gaps.Add(QueueGap.Providers);
            }
        }

        string? trailer = null;
        if (trailers && details is not null)
        {
            try
            {
                trailer = KidTitleDetails.SelectTrailer((await tmdb.GetVideosAsync(type, request.TmdbId, ct: ct)).Results);
            }
            catch (Exception ex) when (LookupFailed(ex, ct))
            {
                this.LogTrailerFailed(ex, request.Kind, request.TmdbId);
                gaps.Add(QueueGap.Trailer);
            }
        }

        bool revokedBefore = revoked.Contains((profile.Id, request.Kind, request.TmdbId));
        QueueReason reason = await this.ExplainAsync(request, profile, details, certification, gaps.Contains(QueueGap.Rating), revokedBefore, ct);
        return new ApprovalQueueItem(
            request.Id,
            profile.Id,
            profile.Name,
            request.Kind,
            request.TmdbId,
            title,
            request.RequestCount,
            request.CreatedAt,
            details is null ? null : KidTitleDetails.Synopsis(details.Overview),
            RatingText(certification),
            Descriptors(certification),
            providers,
            trailer,
            reason,
            gaps);
    }

    private async Task<QueueReason> ExplainAsync(
        MediaRequest request,
        KidProfile profile,
        TmdbDetails? details,
        TmdbCertification? certification,
        bool ratingFailed,
        bool revokedBefore,
        CancellationToken ct)
    {
        if (details is null || ratingFailed)
        {
            return Unavailable(certification);
        }

        TmdbMediaType type = request.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        IReadOnlyList<TmdbKeyword>? keywords = null;
        if (profile.BlockedKeywordIds.Count > 0)
        {
            try
            {
                keywords = await tmdb.GetKeywordsAsync(type, request.TmdbId, ct);
            }
            catch (Exception ex) when (LookupFailed(ex, ct))
            {
                this.LogKeywordsFailed(ex, request.Kind, request.TmdbId);
                return Unavailable(certification);
            }
        }

        IReadOnlyList<int>? genreIds = details.Genres is null ? null : [.. details.Genres.Select(genre => genre.Id)];
        IReadOnlyList<int>? keywordIds = keywords is null ? null : [.. keywords.Select(keyword => keyword.Id)];
        ContentTitle content = new(
            new TitleKey(request.Kind, request.TmdbId),
            certification is null ? [] : [new RegionCertification(certification.Region, certification.Rating)],
            genreIds,
            keywordIds,
            details.OriginalLanguage);
        ContentPolicy policy = ContentPolicy.ForProfile(
            profile.MovieAgeLimit,
            profile.TvAgeLimit,
            profile.BlockedGenreIds,
            profile.BlockedKeywordIds,
            profile.AllowedOriginalLanguages);
        ContentDecision decision = ContentPolicyEngine.Default.Evaluate(content, policy);
        RatingLevel? level = new RatingRule().Evaluate(content, policy).Reason.Level;
        AutoApproveRule rule = new(profile.AutoApprove, profile.AutoApproveMovieRating, profile.AutoApproveTvRating);
        return Explain(decision, rule, request.Kind, level, policy.HasUnknownGenres(genreIds), revokedBefore, certification, details, keywords, policy);
    }

    internal static QueueReason Explain(
        ContentDecision decision,
        AutoApproveRule rule,
        MediaKind kind,
        RatingLevel? level,
        bool genresUnknown,
        bool revokedBefore,
        TmdbCertification? certification,
        TmdbDetails? details,
        IReadOnlyList<TmdbKeyword>? keywords,
        ContentPolicy policy)
    {
        string? rating = ShownRating(decision.Reason.Rating, certification);
        RatingLevel? shownLevel = decision.Reason.Level ?? level;
        if (revokedBefore)
        {
            return new QueueReason(QueueReasonCode.RevokedBefore, rating, shownLevel, null, null, null, null);
        }

        QueueReasonCode? fromPolicy = decision.Outcome switch
        {
            ContentOutcome.NeedsApproval when decision.Reason.Code == ContentReasonCode.Unrated => QueueReasonCode.Unrated,
            ContentOutcome.NeedsApproval when decision.Reason.Code == ContentReasonCode.UnknownGenres => QueueReasonCode.UnknownGenres,
            ContentOutcome.Blocked => BlockedCode(decision.Reason.Code),
            _ => null,
        };
        if (fromPolicy is QueueReasonCode policyCode)
        {
            return Build(policyCode, decision, rating, shownLevel, rule, kind, details, keywords, policy);
        }

        QueueReasonCode code = rule.WhyNot(decision, kind, level, genresUnknown) switch
        {
            AutoApproveSkip.GenresUnknown => QueueReasonCode.UnknownGenres,
            AutoApproveSkip.Unrated => QueueReasonCode.Unrated,
            AutoApproveSkip.AboveCeiling => QueueReasonCode.AboveCeiling,
            AutoApproveSkip.SwitchOff => QueueReasonCode.AutoApproveOff,
            AutoApproveSkip.NotAllowed => QueueReasonCode.Unavailable,
            _ => QueueReasonCode.NotAutoApproved,
        };
        return Build(code, decision, rating, shownLevel, rule, kind, details, keywords, policy);
    }

    private static QueueReason Build(
        QueueReasonCode code,
        ContentDecision decision,
        string? rating,
        RatingLevel? level,
        AutoApproveRule rule,
        MediaKind kind,
        TmdbDetails? details,
        IReadOnlyList<TmdbKeyword>? keywords,
        ContentPolicy policy)
    {
        RatingLevel? max = code switch
        {
            QueueReasonCode.AboveCeiling => kind == MediaKind.Movie ? rule.MaxMovieRating : rule.MaxTvRating,
            QueueReasonCode.AboveRatingLimit => decision.Reason.MaxLevel ?? policy.MaxRatingFor(kind),
            _ => null,
        };
        string? genreName = code == QueueReasonCode.BlockedGenre ? GenreName(details, decision.Reason.GenreId) : null;
        string? keywordName = code == QueueReasonCode.BlockedKeyword ? KeywordName(keywords, decision.Reason.KeywordId) : null;
        string? language = code == QueueReasonCode.LanguageNotAllowed ? decision.Reason.OriginalLanguage : null;
        return new QueueReason(code, rating, level, max, genreName, keywordName, language);
    }

    private static QueueReasonCode BlockedCode(ContentReasonCode code) => code switch
    {
        ContentReasonCode.AboveRatingLimit => QueueReasonCode.AboveRatingLimit,
        ContentReasonCode.BlockedGenre => QueueReasonCode.BlockedGenre,
        ContentReasonCode.BlockedKeyword => QueueReasonCode.BlockedKeyword,
        ContentReasonCode.LanguageNotAllowed => QueueReasonCode.LanguageNotAllowed,
        ContentReasonCode.ExplicitlyBlocked => QueueReasonCode.ExplicitlyBlocked,
        _ => QueueReasonCode.Unavailable,
    };

    private static QueueReason Unavailable(TmdbCertification? certification) =>
        new(QueueReasonCode.Unavailable, RatingText(certification), null, null, null, null, null);

    private static string? ShownRating(string? decided, TmdbCertification? certification)
    {
        string? rating = decided?.Trim();
        if (!string.IsNullOrEmpty(rating))
        {
            return rating;
        }

        return RatingText(certification);
    }

    private static string? RatingText(TmdbCertification? certification)
    {
        string? rating = certification?.Rating?.Trim();
        return string.IsNullOrEmpty(rating) ? null : rating;
    }

    private static IReadOnlyList<string> Descriptors(TmdbCertification? certification)
    {
        if (certification?.Descriptors is null)
        {
            return [];
        }

        List<string> descriptors = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string descriptor in certification.Descriptors)
        {
            string text = descriptor.Trim();
            if (text.Length > 0 && seen.Add(text))
            {
                descriptors.Add(text);
            }
        }

        return descriptors;
    }

    private static string? GenreName(TmdbDetails? details, int? genreId)
    {
        if (genreId is not int id || details?.Genres is null)
        {
            return null;
        }

        string? name = details.Genres.FirstOrDefault(genre => genre.Id == id)?.Name?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? KeywordName(IReadOnlyList<TmdbKeyword>? keywords, int? keywordId)
    {
        if (keywordId is not int id || keywords is null)
        {
            return null;
        }

        string? name = keywords.FirstOrDefault(keyword => keyword.Id == id)?.Name?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static bool LookupFailed(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException;

    private sealed class QueuedRequest
    {
        public required MediaRequest Request { get; init; }

        public required KidProfile Profile { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read details for {Kind} {TmdbId}")]
    private partial void LogDetailsFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read the certification for {Kind} {TmdbId}")]
    private partial void LogCertificationFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read watch providers for {Kind} {TmdbId}")]
    private partial void LogProvidersFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read videos for {Kind} {TmdbId}")]
    private partial void LogTrailerFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read keywords for {Kind} {TmdbId}")]
    private partial void LogKeywordsFailed(Exception exception, MediaKind kind, int tmdbId);
}
