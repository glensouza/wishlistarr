using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Plex;

public static class PlexServiceCollectionExtensions
{
    public static IServiceCollection AddPlex<TSettings>(this IServiceCollection services)
        where TSettings : class, IAppSettings
    {
        services.AddOptions<PlexOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAppSettings, TSettings>();
        services.AddSingleton<PlexTvClient>();
        services.AddSingleton<PlexServerClient>();
        services.AddSingleton<PlexMediaServer>();
        services.AddSingleton<IMediaServer>(provider => provider.GetRequiredService<PlexMediaServer>());
        services.AddSingleton<IMediaServerAccount>(provider => provider.GetRequiredService<PlexMediaServer>());
        services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<PlexMediaServer>());
        services.AddSingleton<IMediaServerTrailers>(provider => provider.GetRequiredService<PlexMediaServer>());
        services.AddSingleton<IMediaServerRestrictions, PlexRestrictionReader>();

        services.AddHttpClient(PlexTvClient.HttpClientName, (provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<PlexOptions>>().Value.PlexTvBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = 5 * 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);

        services.AddHttpClient(PlexServerClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.MaxResponseContentBufferSize = 32 * 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);

        return services;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    };
}
