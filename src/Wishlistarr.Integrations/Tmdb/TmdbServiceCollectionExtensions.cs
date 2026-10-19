using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wishlistarr.Integrations.Tmdb;

public static class TmdbServiceCollectionExtensions
{
    public static IServiceCollection AddTmdb<TKeyProvider>(this IServiceCollection services)
        where TKeyProvider : class, ITmdbApiKeyProvider
    {
        services.AddOptions<TmdbOptions>();
        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITmdbApiKeyProvider, TKeyProvider>();
        services.AddTransient<TmdbRetryHandler>();

        services.AddHttpClient<ITmdbClient, TmdbClient>((provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<TmdbOptions>>().Value.ApiBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<TmdbRetryHandler>();

        services.AddHttpClient<TmdbImageProxy>((provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<TmdbOptions>>().Value.ImageBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
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
