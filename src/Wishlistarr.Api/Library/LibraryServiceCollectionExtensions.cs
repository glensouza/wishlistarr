using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Webhooks;
using Wishlistarr.Core.Library;

namespace Wishlistarr.Api.Library;

public static class LibraryServiceCollectionExtensions
{
    public static IServiceCollection AddLibraryIndex(this IServiceCollection services)
    {
        services.AddSingleton<LibraryRefreshGate>();
        services.AddScoped<LibraryRefresh>();
        services.AddScoped<LibraryIndex>();
        services.AddScoped<ILibraryIndex>(provider => provider.GetRequiredService<LibraryIndex>());
        services.AddScoped<IWebhookHandler, LibraryWebhookHandler>();
        services.AddBackgroundJob<LibraryFullRefreshJob>(
            LibraryFullRefreshJob.JobName,
            "Reads every movie and show from Plex into the library index.",
            TimeSpan.FromDays(1));
        services.AddBackgroundJob<LibraryIncrementalRefreshJob>(
            LibraryIncrementalRefreshJob.JobName,
            "Updates the library index with items Plex changed since the last refresh.",
            TimeSpan.FromMinutes(15));
        return services;
    }
}
