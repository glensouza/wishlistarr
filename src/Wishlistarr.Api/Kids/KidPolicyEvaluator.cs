using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Kids;

public static class KidPolicyEvaluator
{
    public static async Task<PolicyEvaluation?> EvaluateAsync(ITmdbClient tmdb, TitleKey key, ContentPolicy policy, bool revoked, CancellationToken ct)
    {
        TmdbMediaType type = key.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        ContentTitle content = new(key, []);
        string name = string.Empty;
        TmdbDetails? details = null;
        TmdbCertification? certification = null;
        PolicyLookup lookup = PolicyLookup.Details;
        try
        {
            details = await tmdb.GetDetailsAsync(type, key.TmdbId, ct: ct);
            name = (key.Kind == MediaKind.Movie ? details.Title : details.Name)?.Trim() ?? string.Empty;
            if (name.Length == 0) return null;
            if (details.Adult) return new PolicyEvaluation(name, content, policy, Unavailable);
            lookup = PolicyLookup.Certification;
            certification = await tmdb.GetCertificationAsync(type, key.TmdbId, [RatingScale.UsRegion], ct);
            IReadOnlyList<TmdbKeyword>? keywords = null;
            if (policy.BlockedKeywordIds.Count > 0 && !policy.Blocked.Contains(key) && !policy.Allowed.Contains(key))
            {
                lookup = PolicyLookup.Keywords;
                keywords = await tmdb.GetKeywordsAsync(type, key.TmdbId, ct);
            }
            content = new ContentTitle(key,
                certification is null ? [] : [new RegionCertification(certification.Region, certification.Rating)],
                details.Genres?.Select(genre => genre.Id).ToList(), keywords?.Select(keyword => keyword.Id).ToList(), details.OriginalLanguage);
            ContentDecision decision = ContentPolicyEngine.Default.Evaluate(content, policy);
            if (decision.Outcome == ContentOutcome.Blocked && decision.Reason.Code != ContentReasonCode.ExplicitlyBlocked && revoked)
                decision = new ContentDecision(ContentOutcome.NeedsApproval, new ContentReason(ContentReasonCode.PreviouslyRevoked));
            return new PolicyEvaluation(name, content, policy, decision, details, certification, keywords);
        }
        catch (TmdbException ex) when (ex.StatusCode == global::System.Net.HttpStatusCode.NotFound) { return null; }
        catch (Exception ex) when (ex is not TmdbNotConfiguredException && !ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
        {
            return new PolicyEvaluation(name, content, policy, Unavailable, details, certification, FailedLookup: lookup);
        }
    }

    public static bool Visible(ContentDecision decision, bool allowedOnly) =>
        allowedOnly ? decision.Outcome == ContentOutcome.Allowed : decision.Outcome != ContentOutcome.Blocked;

    private static ContentDecision Unavailable => new(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.MetadataUnavailable));
}
