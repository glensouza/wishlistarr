using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Wishlistarr.Integrations.Tmdb;

public sealed class TmdbRetryHandler(IOptions<TmdbOptions> options, TimeProvider time) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TmdbOptions settings = options.Value;
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= settings.MaxRetries)
            {
                return response;
            }

            TimeSpan delay = this.GetDelay(response, attempt, settings);
            response.Dispose();
            await Task.Delay(delay, time, cancellationToken);
        }
    }

    private TimeSpan GetDelay(HttpResponseMessage response, int attempt, TmdbOptions settings)
    {
        TimeSpan delay = settings.RetryBaseDelay * Math.Pow(2, attempt);
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is DateTimeOffset date)
        {
            delay = date - time.GetUtcNow();
        }

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay > settings.MaxRetryDelay ? settings.MaxRetryDelay : delay;
    }
}
