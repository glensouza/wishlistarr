using Wishlistarr.Api.Labels;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public sealed partial class RequestLabeling(MediaRequests requests, LabelWriter labels, ILogger<RequestLabeling> logger)
{
    public const string LabelSource = "request";

    public async Task<RequestResult> LabelAsync(
        int requestId,
        IReadOnlyList<MediaItem> matches,
        KidProfile profile,
        RequestActor actor)
    {
        // The request is already stored as available, so labeling finishes even if the caller goes away.
        CancellationToken ct = CancellationToken.None;
        string? failure = null;
        try
        {
            int labeled = 0;
            foreach (MediaItem match in matches)
            {
                LabelWriteResult written = await labels.AddAsync(match.Id, profile.PlexLabel, profile.Id, LabelSource, ct);
                if (written.Outcome is LabelWriteOutcome.Added or LabelWriteOutcome.AlreadyPresent)
                {
                    labeled++;
                }
            }

            if (labeled == 0)
            {
                failure = "The title is no longer in the library.";
            }
        }
        catch (Exception ex)
        {
            this.LogLabelFailed(ex, requestId);
            failure = "The Plex label could not be written.";
        }

        return await requests.TransitionAsync(
            requestId,
            failure is null ? RequestState.Labeled : RequestState.Failed,
            actor,
            failure,
            ct,
            RequestState.Available);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label for request {RequestId} failed")]
    private partial void LogLabelFailed(Exception ex, int requestId);
}
