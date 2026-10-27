using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;

namespace Wishlistarr.Data;

[TmdbData(nameof(TitleFetchedAt), nameof(Title))]
public sealed class MediaRequest
{
    public int Id { get; set; }

    public int KidProfileId { get; set; }

    public KidProfile? KidProfile { get; set; }

    public MediaKind Kind { get; set; }

    public int TmdbId { get; set; }

    public string? Title { get; set; }

    public DateTimeOffset? TitleFetchedAt { get; set; }

    public RequestState State { get; set; }

    public int RequestCount { get; set; }

    public bool CountsTowardAllowance { get; set; }

    public FulfilmentBackend? Backend { get; set; }

    public int? ExternalId { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastRequestedAt { get; set; }

    public DateTimeOffset StateChangedAt { get; set; }

    public int Version { get; set; }

    public List<MediaRequestEvent> Events { get; } = [];
}
