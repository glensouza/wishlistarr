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

public sealed record KidAllowanceSnapshot(int? Remaining, DateTimeOffset? ResetsAt);

public sealed partial class KidRequestFlow(
    WishlistarrDbContext db,
    MediaRequests requests,
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
            .FirstWithContentRulesAsync(row => row.Id == kidProfileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return new KidRequestResult(KidRequestOutcome.ProfileNotFound);
        }

        PolicyEvaluation? evaluation = await this.EvaluateAsync(profile, title, ct);
        if (evaluation is null)
        {
            return new KidRequestResult(KidRequestOutcome.TitleNotFound);
        }

        string name = evaluation.Name;
        ContentDecision decision = evaluation.Decision;
        bool revokedBefore = await this.WasRevokedAsync(profile.Id, title, ct);
        if (decision.Outcome == ContentOutcome.Blocked)
        {
            this.LogBlocked(profile.Id, title.Kind, title.TmdbId, decision.Reason.Code);
            return new KidRequestResult(KidRequestOutcome.NotAllowed);
        }

        bool auto = profile.AutoApproval().AppliesTo(decision, title.Kind, evaluation.Level, evaluation.GenresUnknown);

        // Both the instant label and auto-approval need a library answer, so not looking up a revoked title keeps it waiting for a parent.
        bool inspectLibrary = !revokedBefore && decision.Outcome == ContentOutcome.Allowed;
        (IReadOnlyList<MediaItem> matches, bool libraryKnown) = inspectLibrary
            ? await this.FindInLibraryAsync(title, ct)
            : ([], false);
        bool inLibrary = libraryKnown && matches.Count > 0;
        bool instant = decision.Outcome == ContentOutcome.Allowed && inLibrary;
        bool autoNow = auto && libraryKnown && !instant;
        bool counts = !instant;

        RequestResult result;
        await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (!revokedBefore && await this.WasRevokedAsync(profile.Id, title, ct))
            {
                // A parent revoked the title after the checks above, so this request waits for a parent too.
                instant = false;
                autoNow = false;
                counts = true;
            }

            bool open = await db.MediaRequests.AnyAsync(
                request => request.KidProfileId == profile.Id
                    && request.Kind == title.Kind
                    && request.TmdbId == title.TmdbId
                    && request.State != RequestState.Denied
                    && request.State != RequestState.Revoked,
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
            RequestResult labeled = (await labeling.LabelAsync(request.Id, matches, profile, InstantLabelActor, ct)).Result;
            return new KidRequestResult(KidRequestOutcome.Created, labeled.Request ?? request);
        }

        if (!autoNow)
        {
            return new KidRequestResult(KidRequestOutcome.Created, request);
        }

        await approval.ApproveAsync(request.Id, AutoApproveActor, RequestApproval.AutoApproveReason, ct);
        return new KidRequestResult(KidRequestOutcome.Created, await requests.GetAsync(request.Id, ct) ?? request);
    }

    public async Task<PolicyEvaluation?> EvaluateAsync(KidProfile profile, TitleKey title, CancellationToken ct) =>
        await Kids.KidPolicyEvaluator.EvaluateAsync(tmdb, title, profile.ContentPolicy(), await this.WasRevokedAsync(profile.Id, title, ct), ct);

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

    public async Task<KidAllowanceSnapshot> AllowanceAsync(KidProfile profile, CancellationToken ct = default)
    {
        if (profile.WeeklyAllowance is not int allowance)
        {
            return new KidAllowanceSnapshot(null, null);
        }

        TimeZoneInfo zone = await this.GetTimeZoneAsync(ct);
        DateTimeOffset now = time.GetUtcNow();
        int used = await this.CountThisWeekAsync(profile.Id, allowance, AllowanceWeek.StartOf(now, zone), ct);
        return new KidAllowanceSnapshot(Math.Max(0, allowance - used), AllowanceWeek.NextStart(now, zone));
    }

    private Task<bool> WasRevokedAsync(int kidProfileId, TitleKey title, CancellationToken ct) =>
        db.MediaRequests.AnyAsync(
            request => request.KidProfileId == kidProfileId
                && request.Kind == title.Kind
                && request.TmdbId == title.TmdbId
                && request.State == RequestState.Revoked,
            ct);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library lookup for {Kind} {TmdbId} failed; treating it as not in the library")]
    private partial void LogLookupFailed(Exception ex, MediaKind kind, int tmdbId);
}

public enum PolicyLookup
{
    Details,
    Certification,
    Keywords,
}

public sealed record PolicyEvaluation(
    string Name,
    ContentTitle Content,
    ContentPolicy Policy,
    ContentDecision Decision,
    TmdbDetails? Details = null,
    TmdbCertification? Certification = null,
    IReadOnlyList<TmdbKeyword>? Keywords = null,
    PolicyLookup? FailedLookup = null)
{
    public RatingLevel? Level => new RatingRule().Evaluate(this.Content, this.Policy).Reason.Level;

    public bool GenresUnknown => this.Policy.HasUnknownGenres(this.Content.GenreIds);
}
