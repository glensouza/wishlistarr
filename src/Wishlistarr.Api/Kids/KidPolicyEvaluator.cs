using System.Text.Json;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Dtdd;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Kids;

public static class KidPolicyEvaluator
{
    public static async Task<PolicyEvaluation?> EvaluateAsync(
        ITmdbClient tmdb,
        TitleKey key,
        ContentPolicy policy,
        bool revoked,
        CancellationToken ct,
        bool keepPartialOnCancel = false,
        IDtddClient? dtdd = null)
    {
        TmdbMediaType type = key.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        ContentTitle content = new(key, []);
        string name = string.Empty;
        TmdbDetails? details = null;
        TmdbCertification? certification = null;
        IReadOnlyList<TmdbKeyword>? keywords = null;
        PolicyLookup lookup = PolicyLookup.Details;
        try
        {
            details = await tmdb.GetDetailsAsync(type, key.TmdbId, ct: ct);
            name = (key.Kind == MediaKind.Movie ? details.Title : details.Name)?.Trim() ?? string.Empty;
            if (name.Length == 0) return null;
            if (details.Adult) return new PolicyEvaluation(name, content, policy, Unavailable);
            lookup = PolicyLookup.Certification;
            certification = await tmdb.GetCertificationAsync(type, key.TmdbId, [RatingScale.UsRegion], ct);
            bool listed = policy.Blocked.Contains(key) || policy.Allowed.Contains(key);
            if (policy.BlockedKeywordIds.Count > 0 && !listed)
            {
                lookup = PolicyLookup.Keywords;
                keywords = await tmdb.GetKeywordsAsync(type, key.TmdbId, ct);
            }

            IReadOnlyList<string>? topics = null;
            if (policy.BlockedTopics.Count > 0 && !listed)
            {
                lookup = PolicyLookup.Topics;
                if (dtdd is null)
                {
                    return new PolicyEvaluation(name, content, policy, Unavailable, details, certification, keywords, lookup);
                }

                topics = await dtdd.GetWarningsAsync(key.Kind, key.TmdbId, ct);
            }

            IReadOnlyList<string>? descriptors = policy.BlockedDescriptors.Count > 0 ? DescriptorList(certification) : null;
            content = new ContentTitle(
                key,
                certification is null ? [] : [new RegionCertification(certification.Region, certification.Rating)],
                details.Genres?.Select(genre => genre.Id).ToList(),
                keywords?.Select(keyword => keyword.Id).ToList(),
                details.OriginalLanguage,
                descriptors,
                topics);
            ContentDecision decision = ContentPolicyEngine.Default.Evaluate(content, policy);
            if (decision.Outcome == ContentOutcome.Blocked && decision.Reason.Code != ContentReasonCode.ExplicitlyBlocked && revoked)
                decision = new ContentDecision(ContentOutcome.NeedsApproval, new ContentReason(ContentReasonCode.PreviouslyRevoked));
            return new PolicyEvaluation(name, content, policy, decision, details, certification, keywords, Topics: topics);
        }
        catch (TmdbException ex) when (ex.StatusCode == global::System.Net.HttpStatusCode.NotFound) { return null; }
        catch (Exception ex) when (ex is not TmdbNotConfiguredException && !ct.IsCancellationRequested && ex is TmdbException or DtddException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PolicyEvaluation(name, content, policy, Unavailable, details, certification, keywords, FailedLookup: lookup);
        }
        catch (OperationCanceledException) when (keepPartialOnCancel)
        {
            return new PolicyEvaluation(name, content, policy, Unavailable, details, certification, keywords, FailedLookup: lookup);
        }
    }

    private static IReadOnlyList<string> DescriptorList(TmdbCertification? certification)
    {
        if (certification?.Descriptors is null)
        {
            return [];
        }

        List<string> descriptors = [];
        foreach (string descriptor in certification.Descriptors)
        {
            string text = descriptor.Trim();
            if (text.Length > 0)
            {
                descriptors.Add(text);
            }
        }

        return descriptors;
    }

    public static bool Visible(ContentDecision decision, bool allowedOnly) =>
        allowedOnly ? decision.Outcome == ContentOutcome.Allowed : decision.Outcome != ContentOutcome.Blocked;

    private static ContentDecision Unavailable => new(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.MetadataUnavailable));
}
