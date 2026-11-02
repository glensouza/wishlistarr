using System.Collections.Concurrent;

namespace Wishlistarr.Api.Requests;

public sealed class RequestDispatchBackoff
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<int, DateTimeOffset> retryAfter = new();

    public IReadOnlyCollection<int> Waiting => [.. this.retryAfter.Keys];

    public bool IsWaiting(int requestId, DateTimeOffset now) =>
        this.retryAfter.TryGetValue(requestId, out DateTimeOffset until) && now < until;

    public void Defer(int requestId, DateTimeOffset now) => this.retryAfter[requestId] = now + Delay;

    public void Clear(int requestId) => this.retryAfter.TryRemove(requestId, out _);
}
