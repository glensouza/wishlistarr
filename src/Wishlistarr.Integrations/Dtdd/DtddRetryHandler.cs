using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Wishlistarr.Integrations.Dtdd;

public sealed class DtddRetryHandler(IOptions<DtddOptions> options, TimeProvider time) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        DtddOptions settings = options.Value;
        TimeSpan waited = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= settings.MaxRetries)
            {
                return response;
            }

            TimeSpan delay = this.GetDelay(response, attempt, settings);
            if (waited + delay > settings.MaxTotalRetryDelay)
            {
                return response;
            }

            waited += delay;
            response.Dispose();
            await Task.Delay(delay, time, cancellationToken);
        }
    }

    private TimeSpan GetDelay(HttpResponseMessage response, int attempt, DtddOptions settings)
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
