using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Seerr;

public static class SeerrServiceCollectionExtensions
{
    public static IServiceCollection AddSeerr<TSettings>(this IServiceCollection services)
        where TSettings : class, IAppSettings
    {
        services.TryAddSingleton<IAppSettings, TSettings>();
        services.AddSingleton<SeerrConnectionStore>();
        services.AddSingleton<SeerrClient>();
        services.AddSingleton<ISeerrBackend, SeerrBackend>();

        services.AddHttpClient(SeerrClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = 32 * 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        return services;
    }
}
