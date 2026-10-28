using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.Titles;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Dtdd;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public enum QueueGap
{
    Synopsis,
    Rating,
    Providers,
    Trailer,
    Keywords,
    ContentWarnings,
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
    BlockedDescriptor,
    BlockedTopic,
}

public sealed record QueueWatchProvider(string Name);

public sealed record QueueReason(
    QueueReasonCode Code,
    string? Rating,
    RatingLevel? Level,
    RatingLevel? MaxLevel,
    string? GenreName,
    string? KeywordName,
    string? Language,
    string? Descriptor = null,
    string? Topic = null);

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
    IReadOnlyList<QueueGap> Gaps,
    IReadOnlyList<string>? ContentWarnings = null,
    ContentWarningCredit? Attribution = null,
    string? ParentNote = null);

public sealed record ContentWarningCredit(string Text, string Url)
{
    public const string Phrase = "Powered by DoesTheDogDie.com";

    public const string Link = "https://www.doesthedogdie.com";

    public static ContentWarningCredit Required { get; } = new(Phrase, Link);
}

public sealed record ApprovalQueuePage(IReadOnlyList<ApprovalQueueItem> Items, int Total, int Limit, int Offset, bool TmdbConfigured = true);

public sealed partial class RequestQueue(
    WishlistarrDbContext db,
    ITmdbClient tmdb,
    ITmdbApiKeyProvider keys,
    IDtddClient dtdd,
    IDtddApiKeyProvider dtddKeys,
    RequestTitles titles,
    SettingsStore settings,
    TimeProvider time,
    ILogger<RequestQueue> logger)
{
    public const int MaxParallelLookups = 4;

    public static readonly TimeSpan PageBudget = TimeSpan.FromSeconds(12);

    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(5);

    public async Task<ApprovalQueuePage?> GetAsync(int? kidProfileId, int limit, int offset, CancellationToken ct)
    {
        if (kidProfileId is int profileId
            && !await db.KidProfiles.AsNoTracking().AnyAsync(profile => profile.Id == profileId, ct))
        {
            return null;
        }

        IQueryable<MediaRequest> matching = db.MediaRequests.AsNoTracking()
            .Where(request => request.State == RequestState.Pending)
            .Where(request => kidProfileId == null || request.KidProfileId == kidProfileId)
            .Join(
                db.KidProfiles.AsNoTracking(),
                request => request.KidProfileId,
                profile => profile.Id,
                (request, profile) => request);

        int total;
        List<MediaRequest> listed;
        Dictionary<int, KidProfile> profiles;
        await db.Database.OpenConnectionAsync(ct);
        await using (SqliteTransaction snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true))
        await using (await db.Database.UseTransactionAsync(snapshot, ct))
        {
            total = await matching.CountAsync(ct);
            listed = await matching
                .OrderBy(request => request.Id)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(ct);
            int[] profileIds = [.. listed.Select(request => request.KidProfileId).Distinct()];
            profiles = (await db.KidProfiles.AsNoTracking()
                .ListWithContentRulesAsync(profile => profileIds.Contains(profile.Id), ct))
                .ToDictionary(profile => profile.Id);
        }

        bool configured = await keys.GetApiKeyAsync(ct) is { Length: > 0 };
        bool warningsConfigured = await dtddKeys.GetApiKeyAsync(ct) is { Length: > 0 };
        if (listed.Count == 0)
        {
            return new ApprovalQueuePage([], total, limit, offset, configured);
        }

        DateTimeOffset staleBefore = await titles.StaleBeforeAsync(ct);
        HashSet<(int ProfileId, MediaKind Kind, int TmdbId)> revoked = await this.RevokedAsync([.. profiles.Keys], ct);
        Dictionary<(MediaKind Kind, int TmdbId), string> notes = await this.NotesAsync(listed, ct);
        bool trailers = YoutubeTrailers.IsEnabled(await settings.GetAsync(YoutubeTrailers.SettingKey, ct));
        TitleLookup[] found = new TitleLookup[listed.Count];
        using (CancellationTokenSource deadline = new(PageBudget, time))
        using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            LimitedTmdbClient client = new(tmdb, time);
            LimitedDtddClient warnings = new(dtdd, time);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, listed.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = ct },
                async (index, _) =>
                {
                    MediaRequest request = listed[index];
                    bool revokedBefore = revoked.Contains((request.KidProfileId, request.Kind, request.TmdbId));
                    TitleLookup lookup = configured
                        ? await this.LookUpAsync(client, warnings, warningsConfigured, request, profiles[request.KidProfileId], revokedBefore, trailers, budget.Token, ct)
                        : new TitleLookup(null, revokedBefore, [], null, MissingDetails(trailers));
                    found[index] = await this.WithWarningsAsync(warnings, warningsConfigured, request, lookup, budget.Token, ct);
                });
            if (deadline.IsCancellationRequested)
            {
                this.LogBudgetExceeded(PageBudget.TotalSeconds);
            }
        }

        List<ApprovalQueueItem> items = [];
        for (int index = 0; index < listed.Count; index++)
        {
            MediaRequest request = listed[index];
            TitleLookup lookup = found[index];
            string title = RequestTitles.Fresh(request, staleBefore)
                ?? (lookup.Evaluation is null ? string.Empty : await titles.StoreAsync(request.Id, lookup.Evaluation.Name, ct));
            notes.TryGetValue((request.Kind, request.TmdbId), out string? note);
            items.Add(Describe(request, profiles[request.KidProfileId], title, lookup, note));
        }

        return new ApprovalQueuePage(items, total, limit, offset, configured);
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

    internal static QueueReason Explain(PolicyEvaluation evaluation, AutoApproveRule rule, MediaKind kind, bool revokedBefore)
    {
        ContentDecision decision = evaluation.Decision;
        TmdbCertification? certification = evaluation.Certification;
        if (decision.Reason.Code == ContentReasonCode.MetadataUnavailable)
        {
            return Unavailable(certification);
        }

        RatingLevel? level = evaluation.Level;
        string? rating = ShownRating(decision.Reason.Rating, certification);
        RatingLevel? shownLevel = decision.Reason.Level ?? level;
        if (revokedBefore && decision.Outcome != ContentOutcome.Blocked)
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
            return Build(policyCode, rating, shownLevel, rule, kind, evaluation);
        }

        QueueReasonCode code = rule.WhyNot(decision, kind, level, evaluation.GenresUnknown) switch
        {
            AutoApproveSkip.GenresUnknown => QueueReasonCode.UnknownGenres,
            AutoApproveSkip.Unrated => QueueReasonCode.Unrated,
            AutoApproveSkip.AboveCeiling => QueueReasonCode.AboveCeiling,
            AutoApproveSkip.SwitchOff => QueueReasonCode.AutoApproveOff,
            AutoApproveSkip.NotAllowed => QueueReasonCode.Unavailable,
            _ => QueueReasonCode.NotAutoApproved,
        };
        return Build(code, rating, shownLevel, rule, kind, evaluation);
    }

    private static List<QueueGap> MissingDetails(bool trailers)
    {
        List<QueueGap> gaps = [QueueGap.Synopsis, QueueGap.Rating, QueueGap.Providers];
        if (trailers)
        {
            gaps.Add(QueueGap.Trailer);
        }

        return gaps;
    }

    private static ApprovalQueueItem Describe(MediaRequest request, KidProfile profile, string title, TitleLookup lookup, string? parentNote)
    {
        PolicyEvaluation? evaluation = lookup.Evaluation;
        TmdbCertification? certification = evaluation?.Certification;
        QueueReason reason = evaluation is null
            ? Unavailable(null)
            : Explain(evaluation, profile.AutoApproval(), request.Kind, lookup.RevokedBefore);
        IReadOnlyList<string>? warnings = lookup.Warnings;
        return new ApprovalQueueItem(
            request.Id,
            profile.Id,
            profile.Name,
            request.Kind,
            request.TmdbId,
            title,
            request.RequestCount,
            request.CreatedAt,
            evaluation?.Details is TmdbDetails details ? KidTitleDetails.Synopsis(details.Overview) : null,
            RatingText(certification),
            Descriptors(certification),
            lookup.Providers,
            lookup.Trailer,
            reason,
            lookup.Gaps,
            warnings,
            warnings is { Count: > 0 } ? ContentWarningCredit.Required : null,
            parentNote);
    }

    private static QueueReason Build(
        QueueReasonCode code,
        string? rating,
        RatingLevel? level,
        AutoApproveRule rule,
        MediaKind kind,
        PolicyEvaluation evaluation)
    {
        ContentReason decided = evaluation.Decision.Reason;
        RatingLevel? max = code switch
        {
            QueueReasonCode.AboveCeiling => kind == MediaKind.Movie ? rule.MaxMovieRating : rule.MaxTvRating,
            QueueReasonCode.AboveRatingLimit => decided.MaxLevel ?? evaluation.Policy.MaxRatingFor(kind),
            _ => null,
        };
        string? genreName = code == QueueReasonCode.BlockedGenre ? GenreName(evaluation.Details, decided.GenreId) : null;
        string? keywordName = code == QueueReasonCode.BlockedKeyword ? KeywordName(evaluation.Keywords, decided.KeywordId) : null;
        string? language = code == QueueReasonCode.LanguageNotAllowed ? decided.OriginalLanguage : null;
        string? descriptor = code == QueueReasonCode.BlockedDescriptor ? decided.Descriptor : null;
        string? topic = code == QueueReasonCode.BlockedTopic ? decided.Topic : null;
        return new QueueReason(code, rating, level, max, genreName, keywordName, language, descriptor, topic);
    }

    private static QueueReasonCode BlockedCode(ContentReasonCode code) => code switch
    {
        ContentReasonCode.AboveRatingLimit => QueueReasonCode.AboveRatingLimit,
        ContentReasonCode.BlockedGenre => QueueReasonCode.BlockedGenre,
        ContentReasonCode.BlockedKeyword => QueueReasonCode.BlockedKeyword,
        ContentReasonCode.LanguageNotAllowed => QueueReasonCode.LanguageNotAllowed,
        ContentReasonCode.ExplicitlyBlocked => QueueReasonCode.ExplicitlyBlocked,
        ContentReasonCode.BlockedDescriptor => QueueReasonCode.BlockedDescriptor,
        ContentReasonCode.BlockedTopic => QueueReasonCode.BlockedTopic,
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
        !ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or OperationCanceledException;

    private async Task<HashSet<(int ProfileId, MediaKind Kind, int TmdbId)>> RevokedAsync(int[] profileIds, CancellationToken ct)
    {
        var rows = await db.MediaRequests.AsNoTracking()
            .Where(request => request.State == RequestState.Revoked && profileIds.Contains(request.KidProfileId))
            .Select(request => new { request.KidProfileId, request.Kind, request.TmdbId })
            .ToListAsync(ct);
        return [.. rows.Select(row => (row.KidProfileId, row.Kind, row.TmdbId))];
    }

    private async Task<Dictionary<(MediaKind Kind, int TmdbId), string>> NotesAsync(List<MediaRequest> listed, CancellationToken ct)
    {
        int[] ids = [.. listed.Select(request => request.TmdbId).Distinct()];
        List<ParentNote> rows = await db.ParentNotes.AsNoTracking()
            .Where(note => ids.Contains(note.TmdbId))
            .ToListAsync(ct);
        Dictionary<(MediaKind Kind, int TmdbId), string> notes = [];
        foreach (ParentNote note in rows)
        {
            notes[(note.Kind, note.TmdbId)] = note.Text;
        }

        return notes;
    }

    private async Task<TitleLookup> WithWarningsAsync(
        IDtddClient warnings,
        bool configured,
        MediaRequest request,
        TitleLookup lookup,
        CancellationToken budget,
        CancellationToken ct)
    {
        if (!configured || ct.IsCancellationRequested)
        {
            return lookup;
        }

        if (lookup.Evaluation?.Topics is not null)
        {
            return lookup with { Warnings = lookup.Evaluation.Topics };
        }

        if (lookup.Evaluation?.FailedLookup == PolicyLookup.Topics || budget.IsCancellationRequested)
        {
            return WarningGap(lookup);
        }

        try
        {
            IReadOnlyList<string> names = await warnings.GetWarningsAsync(request.Kind, request.TmdbId, budget);
            ct.ThrowIfCancellationRequested();
            return lookup with { Warnings = names };
        }
        catch (Exception ex) when (WarningFailed(ex, ct))
        {
            if (budget.IsCancellationRequested)
            {
                this.LogSkipped(request.Kind, request.TmdbId);
            }
            else
            {
                this.LogWarningsFailed(ex, request.Kind, request.TmdbId);
            }

            return WarningGap(lookup);
        }
    }

    private static TitleLookup WarningGap(TitleLookup lookup) =>
        lookup.Gaps.Contains(QueueGap.ContentWarnings)
            ? lookup
            : lookup with { Gaps = [.. lookup.Gaps, QueueGap.ContentWarnings] };

    private static bool WarningFailed(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is DtddException or HttpRequestException or OperationCanceledException or JsonException;

    private async Task<TitleLookup> LookUpAsync(
        ITmdbClient client,
        IDtddClient warnings,
        bool warningsConfigured,
        MediaRequest request,
        KidProfile profile,
        bool revokedBefore,
        bool trailers,
        CancellationToken budget,
        CancellationToken ct)
    {
        TitleKey key = new(request.Kind, request.TmdbId);
        TmdbMediaType type = request.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        PolicyEvaluation? evaluation = null;
        try
        {
            evaluation = await KidPolicyEvaluator.EvaluateAsync(
                client,
                key,
                profile.ContentPolicy(),
                revokedBefore,
                budget,
                keepPartialOnCancel: true,
                dtdd: warningsConfigured ? warnings : null);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (LookupFailed(ex, ct))
        {
            this.LogDetailsFailed(ex, request.Kind, request.TmdbId);
        }

        List<QueueGap> gaps = [];
        if (evaluation is null || evaluation.FailedLookup == PolicyLookup.Details)
        {
            return new TitleLookup(evaluation, revokedBefore, [], null, MissingDetails(trailers));
        }

        if (evaluation.FailedLookup is PolicyLookup failed)
        {
            if (budget.IsCancellationRequested)
            {
                this.LogSkipped(request.Kind, request.TmdbId);
            }
            else
            {
                this.LogLookupFailed(failed, request.Kind, request.TmdbId);
            }

            if (failed != PolicyLookup.Topics)
            {
                gaps.Add(failed == PolicyLookup.Certification ? QueueGap.Rating : QueueGap.Keywords);
            }
        }

        if (evaluation.Details is null)
        {
            return new TitleLookup(evaluation, revokedBefore, [], null, gaps);
        }

        IReadOnlyList<QueueWatchProvider> providers = [];
        try
        {
            providers = [.. StreamingNames(await client.GetWatchProvidersAsync(type, request.TmdbId, budget))
                .Select(name => new QueueWatchProvider(name))];
        }
        catch (Exception ex) when (LookupFailed(ex, ct))
        {
            this.LogFailure(budget, ex, request, this.LogProvidersFailed);
            gaps.Add(QueueGap.Providers);
        }

        string? trailer = null;
        if (trailers)
        {
            try
            {
                trailer = KidTitleDetails.SelectTrailer((await client.GetVideosAsync(type, request.TmdbId, ct: budget)).Results);
            }
            catch (Exception ex) when (LookupFailed(ex, ct))
            {
                this.LogFailure(budget, ex, request, this.LogTrailerFailed);
                gaps.Add(QueueGap.Trailer);
            }
        }

        return new TitleLookup(evaluation, revokedBefore, providers, trailer, gaps);
    }

    private void LogFailure(CancellationToken budget, Exception ex, MediaRequest request, Action<Exception, MediaKind, int> warn)
    {
        if (budget.IsCancellationRequested)
        {
            this.LogSkipped(request.Kind, request.TmdbId);
        }
        else
        {
            warn(ex, request.Kind, request.TmdbId);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Approval queue skipped part of {Kind} {TmdbId} because the page budget ran out")]
    private partial void LogSkipped(MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read details for {Kind} {TmdbId}")]
    private partial void LogDetailsFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read the {Lookup} for {Kind} {TmdbId}")]
    private partial void LogLookupFailed(PolicyLookup lookup, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read content warnings for {Kind} {TmdbId}")]
    private partial void LogWarningsFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read watch providers for {Kind} {TmdbId}")]
    private partial void LogProvidersFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue could not read videos for {Kind} {TmdbId}")]
    private partial void LogTrailerFailed(Exception exception, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue stopped calling TMDB after {Seconds} seconds and showed the rest of the page with gaps")]
    private partial void LogBudgetExceeded(double seconds);

    private sealed record TitleLookup(
        PolicyEvaluation? Evaluation,
        bool RevokedBefore,
        IReadOnlyList<QueueWatchProvider> Providers,
        string? Trailer,
        IReadOnlyList<QueueGap> Gaps,
        IReadOnlyList<string>? Warnings = null);

    private sealed class LimitedTmdbClient(ITmdbClient inner, TimeProvider time) : ITmdbClient
    {
        public Task<bool> TestApiKeyAsync(CancellationToken ct = default) =>
            this.RunAsync(inner.TestApiKeyAsync, ct);

        public Task<TmdbPage<TmdbSearchResult>> SearchAsync(string query, TmdbMediaType? type = null, int page = 1, string language = "en-US", CancellationToken ct = default) =>
            this.RunAsync(token => inner.SearchAsync(query, type, page, language, token), ct);

        public Task<TmdbDetails> GetDetailsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetDetailsAsync(type, id, language, token), ct);

        public Task<IReadOnlyList<TmdbKeyword>> GetKeywordsAsync(TmdbMediaType type, int id, CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetKeywordsAsync(type, id, token), ct);

        public Task<TmdbCredits> GetCreditsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetCreditsAsync(type, id, language, token), ct);

        public Task<TmdbCertification?> GetCertificationAsync(TmdbMediaType type, int id, IReadOnlyList<string> regions, CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetCertificationAsync(type, id, regions, token), ct);

        public Task<TmdbVideos> GetVideosAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetVideosAsync(type, id, language, token), ct);

        public Task<TmdbImages> GetImagesAsync(TmdbMediaType type, int id, string imageLanguages = "en,null", CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetImagesAsync(type, id, imageLanguages, token), ct);

        public Task<TmdbPage<TmdbSearchResult>> DiscoverAsync(TmdbMediaType type, TmdbDiscoverQuery query, string language = "en-US", CancellationToken ct = default) =>
            this.RunAsync(token => inner.DiscoverAsync(type, query, language, token), ct);

        public Task<TmdbPage<TmdbSearchResult>> GetTrendingAsync(TmdbMediaType type, string window = "week", int page = 1, string language = "en-US", CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetTrendingAsync(type, window, page, language, token), ct);

        public Task<TmdbWatchProviders> GetWatchProvidersAsync(TmdbMediaType type, int id, CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetWatchProvidersAsync(type, id, token), ct);

        public Task<int?> FindIdAsync(TmdbMediaType type, ExternalIdSource source, string externalId, CancellationToken ct = default) =>
            this.RunAsync(token => inner.FindIdAsync(type, source, externalId, token), ct);

        public Task<IReadOnlyList<TmdbListedGenre>> GetGenresAsync(string language = "en-US", CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetGenresAsync(language, token), ct);

        private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using CancellationTokenSource limit = new(CallLimit, time);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            try
            {
                return await call(linked.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Shaped like an HttpClient timeout so the shared evaluator treats it as a failed lookup rather than a cancellation.
                throw new TaskCanceledException($"TMDB did not answer within {CallLimit.TotalSeconds} seconds.", new TimeoutException());
            }
        }
    }

    private sealed class LimitedDtddClient(IDtddClient inner, TimeProvider time) : IDtddClient
    {
        public Task<bool> TestApiKeyAsync(CancellationToken ct = default) => inner.TestApiKeyAsync(ct);

        public Task<IReadOnlyList<string>> GetWarningsAsync(MediaKind kind, int tmdbId, CancellationToken ct = default) =>
            this.RunAsync(token => inner.GetWarningsAsync(kind, tmdbId, token), ct);

        private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using CancellationTokenSource limit = new(CallLimit, time);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            try
            {
                return await call(linked.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TaskCanceledException($"DoesTheDogDie did not answer within {CallLimit.TotalSeconds} seconds.", new TimeoutException());
            }
        }
    }
}
