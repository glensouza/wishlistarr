using Wishlistarr.Core.Requests;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestStateMachineTests
{
    private static readonly (RequestState From, RequestState To, RequestActorKind[] Actors)[] Expected =
    [
        (RequestState.Pending, RequestState.Approved, [RequestActorKind.Parent, RequestActorKind.System]),
        (RequestState.Pending, RequestState.Denied, [RequestActorKind.Parent, RequestActorKind.System]),
        (RequestState.Approved, RequestState.Fulfilling, [RequestActorKind.System]),
        (RequestState.Approved, RequestState.Available, [RequestActorKind.System]),
        (RequestState.Approved, RequestState.Failed, [RequestActorKind.System]),
        (RequestState.Fulfilling, RequestState.Available, [RequestActorKind.System]),
        (RequestState.Fulfilling, RequestState.Failed, [RequestActorKind.System]),
        (RequestState.Available, RequestState.Labeled, [RequestActorKind.System]),
        (RequestState.Available, RequestState.Failed, [RequestActorKind.System]),
        (RequestState.Labeled, RequestState.Revoked, [RequestActorKind.Parent]),
        (RequestState.Failed, RequestState.Fulfilling, [RequestActorKind.Parent, RequestActorKind.System]),
        (RequestState.Failed, RequestState.Available, [RequestActorKind.Parent, RequestActorKind.System]),
        (RequestState.Failed, RequestState.Denied, [RequestActorKind.Parent]),
    ];

    [Fact]
    public void Only_the_listed_transitions_and_actors_are_allowed()
    {
        foreach (RequestState from in Enum.GetValues<RequestState>())
        {
            foreach (RequestState to in Enum.GetValues<RequestState>())
            {
                foreach (RequestActorKind actor in Enum.GetValues<RequestActorKind>())
                {
                    bool expected = Expected.Any(row => row.From == from && row.To == to && row.Actors.Contains(actor));
                    Assert.True(
                        expected == RequestStateMachine.IsAllowed(from, to, actor),
                        $"{actor} {from} -> {to} should be {(expected ? "allowed" : "rejected")}");
                }
            }
        }
    }

    [Fact]
    public void Kids_cannot_move_requests() =>
        Assert.DoesNotContain(RequestStateMachine.Transitions, transition => transition.Actors.Contains(RequestActorKind.Kid));

    [Fact]
    public void Denied_and_revoked_are_final()
    {
        RequestState[] terminal = [.. Enum.GetValues<RequestState>().Where(RequestStateMachine.IsTerminal)];

        Assert.Equal([RequestState.Denied, RequestState.Revoked], terminal);
    }

    [Theory]
    [InlineData(RequestState.Approved, RequestState.Fulfilling)]
    [InlineData(RequestState.Fulfilling, RequestState.Fulfilling)]
    [InlineData(RequestState.Available, RequestState.Available)]
    public void Retries_return_to_the_step_that_failed(RequestState failedFrom, RequestState expected) =>
        Assert.Equal(expected, RequestStateMachine.RetryTargetFor(failedFrom));
}
