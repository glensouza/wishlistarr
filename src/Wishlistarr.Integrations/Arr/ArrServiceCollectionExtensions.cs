using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Arr;

public static class ArrServiceCollectionExtensions
{
    public static IServiceCollection AddArr<TSettings>(this IServiceCollection services)
        where TSettings : class, IAppSettings
    {
        services.TryAddSingleton<IAppSettings, TSettings>();
        services.AddSingleton<ArrConnectionStore>();
        services.AddSingleton<ArrClient>();
        services.AddSingleton<IArrBackend, SonarrBackend>();
        services.AddSingleton<IArrBackend, RadarrBackend>();
        services.AddSingleton<ArrBackends>();

        services.AddHttpClient(ArrClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = 32 * 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            });

        return services;
    }
}
