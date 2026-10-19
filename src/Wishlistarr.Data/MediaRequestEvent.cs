using Wishlistarr.Core.Requests;

namespace Wishlistarr.Data;

public sealed class MediaRequestEvent
{
    public int Id { get; set; }

    public int MediaRequestId { get; set; }

    public MediaRequest? MediaRequest { get; set; }

    public RequestState? FromState { get; set; }

    public RequestState ToState { get; set; }

    public RequestActorKind ActorKind { get; set; }

    public int? ActorId { get; set; }

    public required string ActorName { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
