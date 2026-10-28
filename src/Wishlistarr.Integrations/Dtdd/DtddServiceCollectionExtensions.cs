using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wishlistarr.Integrations.Dtdd;

public static class DtddServiceCollectionExtensions
{
    public static IServiceCollection AddDoesTheDogDie<TKeyProvider>(this IServiceCollection services)
        where TKeyProvider : class, IDtddApiKeyProvider
    {
        services.AddOptions<DtddOptions>();
        services.AddSingleton<DtddCache>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDtddApiKeyProvider, TKeyProvider>();
        services.AddTransient<DtddRetryHandler>();

        services.AddHttpClient<IDtddClient, DtddClient>((provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<DtddOptions>>().Value.ApiBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(10);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<DtddRetryHandler>();

        return services;
    }
}
