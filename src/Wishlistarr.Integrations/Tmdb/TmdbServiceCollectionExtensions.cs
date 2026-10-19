using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wishlistarr.Integrations.Tmdb;

public static class TmdbServiceCollectionExtensions
{
    public static IServiceCollection AddTmdb<TKeyProvider>(this IServiceCollection services)
        where TKeyProvider : class, ITmdbApiKeyProvider
    {
        services.AddOptions<TmdbOptions>();
        services.AddSingleton<TmdbCache>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITmdbApiKeyProvider, TKeyProvider>();
        services.AddTransient<TmdbRetryHandler>();

        services.AddHttpClient<ITmdbClient, TmdbClient>((provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<TmdbOptions>>().Value.ApiBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(120);
                client.MaxResponseContentBufferSize = 5 * 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<TmdbRetryHandler>();

        services.AddHttpClient<TmdbImageProxy>((provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<TmdbOptions>>().Value.ImageBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = TmdbImageProxy.MaxBytes;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
            });

        return services;
    }
}
