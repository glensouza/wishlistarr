using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Requests;

public enum KidRequestOutcome
{
    Created,
    Merged,
    NotAllowed,
    AllowanceUsed,
    TitleNotFound,
    ProfileNotFound,
}

public sealed record KidRequestResult(KidRequestOutcome Outcome, MediaRequest? Request = null, DateTimeOffset? AllowanceResetsAt = null);

public sealed partial class KidRequestFlow(
    WishlistarrDbContext db,
    MediaRequests requests,
    RequestTitles titles,
    ITmdbClient tmdb,
    LibraryLookup library,
    RequestLabeling labeling,
    RequestApproval approval,
    SettingsStore settings,
    TimeProvider time,
    ILogger<KidRequestFlow> logger)
{
    public const string TimeZoneSetting = "parent.time_zone";

    public static readonly RequestActor InstantLabelActor = RequestActor.System("instant-label");

    public static readonly RequestActor AutoApproveActor = RequestActor.System("auto-approve");

    public static bool TryFindTimeZone(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public async Task<KidRequestResult> SubmitAsync(int kidProfileId, TitleKey title, CancellationToken ct = default)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == kidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return new KidRequestResult(KidRequestOutcome.ProfileNotFound);
        }

        TmdbMediaType type = title.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        if (await titles.FetchAsync(title.Kind, title.TmdbId, ct) is not (string name, TmdbDetails details))
        {
            return new KidRequestResult(KidRequestOutcome.TitleNotFound);
        }

        TmdbCertification? certification = await tmdb.GetCertificationAsync(type, title.TmdbId, [RatingScale.UsRegion], ct);
        IReadOnlyList<int>? keywordIds = null;
        if (profile.BlockedKeywordIds.Count > 0)
        {
            try
            {
                keywordIds = [.. (await tmdb.GetKeywordsAsync(type, title.TmdbId, ct)).Select(keyword => keyword.Id)];
            }
            catch (Exception ex) when (ex is not TmdbNotConfiguredException
                && !ct.IsCancellationRequested
                && ex is TmdbException or HttpRequestException or TaskCanceledException)
            {
                this.LogKeywordLookupFailed(ex, title.Kind, title.TmdbId);
                return new KidRequestResult(KidRequestOutcome.NotAllowed);
            }
        }

        ContentTitle content = new(
            title,
            certification is null ? [] : [new RegionCertification(certification.Region, certification.Rating)],
            details.Genres is null ? null : [.. details.Genres.Select(genre => genre.Id)],
            keywordIds,
            details.OriginalLanguage);
        ContentPolicy policy = ContentPolicy.ForProfile(
            profile.MovieAgeLimit,
            profile.TvAgeLimit,
            profile.BlockedGenreIds,
            profile.BlockedKeywordIds,
            profile.AllowedOriginalLanguages);
        ContentDecision decision = ContentPolicyEngine.Default.Evaluate(content, policy);
        if (decision.Outcome == ContentOutcome.Blocked)
        {
            this.LogBlocked(profile.Id, title.Kind, title.TmdbId, decision.Reason.Code);
            return new KidRequestResult(KidRequestOutcome.NotAllowed);
        }

        RatingLevel? rating = new RatingRule().Evaluate(content, policy).Reason.Level;
        bool genresUnknown = profile.BlockedGenreIds.Count > 0 && content.GenreIds is not { Count: > 0 };
        bool auto = new AutoApproveRule(
            profile.AutoApprove,
            profile.AutoApproveMovieRating,
            profile.AutoApproveTvRating).AppliesTo(decision, title.Kind, rating, genresUnknown);
        (IReadOnlyList<MediaItem> matches, bool libraryKnown) = decision.Outcome == ContentOutcome.Allowed
            ? await this.FindInLibraryAsync(title, ct)
            : ([], false);
        bool inLibrary = libraryKnown && matches.Count > 0;
        bool instant = decision.Outcome == ContentOutcome.Allowed && inLibrary;
        bool autoNow = auto && libraryKnown && !instant;
        bool counts = !instant;

        RequestResult result;
        await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct))
        {
            bool open = await db.MediaRequests.AnyAsync(
                request => request.KidProfileId == profile.Id
                    && request.Kind == title.Kind
                    && request.TmdbId == title.TmdbId
                    && request.State != RequestState.Denied,
                ct);
            if (!open && counts && profile.WeeklyAllowance is int allowance)
            {
                TimeZoneInfo zone = await this.GetTimeZoneAsync(ct);
                DateTimeOffset now = time.GetUtcNow();
                if (await this.CountThisWeekAsync(profile.Id, allowance, AllowanceWeek.StartOf(now, zone), ct) >= allowance)
                {
                    return new KidRequestResult(KidRequestOutcome.AllowanceUsed, AllowanceResetsAt: AllowanceWeek.NextStart(now, zone));
                }
            }

            result = await requests.CreateAsync(
                profile.Id,
                title,
                name,
                RequestActor.Kid(profile.Id, profile.Name),
                countsTowardAllowance: counts,
                ct);
            if (result.Outcome == RequestOutcome.Created && instant)
            {
                await requests.TransitionAsync(result.Request!.Id, RequestState.Approved, InstantLabelActor, "Allowed and already in the library.", ct);
                await requests.TransitionAsync(result.Request.Id, RequestState.Available, InstantLabelActor, ct: ct);
            }

            await transaction.CommitAsync(ct);
        }

        if (result is not { Outcome: RequestOutcome.Created or RequestOutcome.Merged, Request: MediaRequest request })
        {
            return new KidRequestResult(KidRequestOutcome.ProfileNotFound);
        }

        if (result.Outcome == RequestOutcome.Merged)
        {
            return new KidRequestResult(KidRequestOutcome.Merged, request);
        }

        if (instant)
        {
            RequestResult labeled = await labeling.LabelAsync(request.Id, matches, profile, InstantLabelActor);
            return new KidRequestResult(KidRequestOutcome.Created, labeled.Request ?? request);
        }

        if (!autoNow)
        {
            return new KidRequestResult(KidRequestOutcome.Created, request);
        }

        await approval.ApproveAsync(request.Id, AutoApproveActor, RequestApproval.AutoApproveReason, ct);
        return new KidRequestResult(KidRequestOutcome.Created, await requests.GetAsync(request.Id, ct) ?? request);
    }

    private async Task<(IReadOnlyList<MediaItem> Items, bool Known)> FindInLibraryAsync(TitleKey title, CancellationToken ct)
    {
        try
        {
            return (await library.FindAsync(title, ct), true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException)
        {
            this.LogLookupFailed(ex, title.Kind, title.TmdbId);
            return ([], false);
        }
    }

    private async Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken ct) =>
        TryFindTimeZone(await settings.GetAsync(TimeZoneSetting, ct), out TimeZoneInfo? zone) ? zone : time.LocalTimeZone;

    private async Task<int> CountThisWeekAsync(int kidProfileId, int allowance, DateTimeOffset weekStart, CancellationToken ct)
    {
        // Only the latest rows can fall in the current week, and SQLite cannot compare the stored offsets, so the check runs in memory.
        List<DateTimeOffset> latest = await db.MediaRequests.AsNoTracking()
            .Where(request => request.KidProfileId == kidProfileId && request.CountsTowardAllowance)
            .OrderByDescending(request => request.Id)
            .Take(allowance)
            .Select(request => request.CreatedAt)
            .ToListAsync(ct);
        return latest.Count(createdAt => createdAt >= weekStart);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request by profile {ProfileId} for {Kind} {TmdbId} was blocked by content policy ({Reason})")]
    private partial void LogBlocked(int profileId, MediaKind kind, int tmdbId, ContentReasonCode reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keyword lookup failed for {Kind} {TmdbId}; the request was refused")]
    private partial void LogKeywordLookupFailed(Exception ex, MediaKind kind, int tmdbId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library lookup for {Kind} {TmdbId} failed; treating it as not in the library")]
    private partial void LogLookupFailed(Exception ex, MediaKind kind, int tmdbId);
}
