using System.Globalization;
using Wishlistarr.Api.Labels;

namespace Wishlistarr.Api.Requests;

public sealed class RequestLocks
{
    private readonly ItemLocks locks = new();

    public Task<IDisposable> AcquireAsync(int requestId, CancellationToken ct = default) =>
        this.locks.AcquireAsync(requestId.ToString(CultureInfo.InvariantCulture), ct);
}
