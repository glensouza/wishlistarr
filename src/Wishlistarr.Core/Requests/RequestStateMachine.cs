namespace Wishlistarr.Core.Requests;

public enum RequestState
{
    Pending,
    Approved,
    Denied,
    Fulfilling,
    Available,
    Labeled,
    Failed,
}

public sealed record RequestTransition(RequestState From, RequestState To, IReadOnlySet<RequestActorKind> Actors);

public static class RequestStateMachine
{
    private static readonly IReadOnlySet<RequestActorKind> ParentOrSystem = new HashSet<RequestActorKind> { RequestActorKind.Parent, RequestActorKind.System };
    private static readonly IReadOnlySet<RequestActorKind> SystemOnly = new HashSet<RequestActorKind> { RequestActorKind.System };
    private static readonly IReadOnlySet<RequestActorKind> ParentOnly = new HashSet<RequestActorKind> { RequestActorKind.Parent };

    public static IReadOnlyList<RequestTransition> Transitions { get; } =
    [
        new(RequestState.Pending, RequestState.Approved, ParentOrSystem),
        new(RequestState.Pending, RequestState.Denied, ParentOrSystem),
        new(RequestState.Approved, RequestState.Fulfilling, SystemOnly),
        new(RequestState.Approved, RequestState.Available, SystemOnly),
        new(RequestState.Approved, RequestState.Failed, SystemOnly),
        new(RequestState.Fulfilling, RequestState.Available, SystemOnly),
        new(RequestState.Fulfilling, RequestState.Failed, SystemOnly),
        new(RequestState.Available, RequestState.Labeled, SystemOnly),
        new(RequestState.Available, RequestState.Failed, SystemOnly),
        new(RequestState.Failed, RequestState.Fulfilling, ParentOrSystem),
        new(RequestState.Failed, RequestState.Available, ParentOrSystem),
        new(RequestState.Failed, RequestState.Denied, ParentOnly),
    ];

    public static RequestTransition? Find(RequestState from, RequestState to) =>
        Transitions.FirstOrDefault(transition => transition.From == from && transition.To == to);

    public static bool IsAllowed(RequestState from, RequestState to, RequestActorKind actor) =>
        Find(from, to)?.Actors.Contains(actor) == true;

    public static bool IsTerminal(RequestState state) => Transitions.All(transition => transition.From != state);

    public static RequestState RetryTargetFor(RequestState failedFrom) =>
        failedFrom == RequestState.Available ? RequestState.Available : RequestState.Fulfilling;
}
