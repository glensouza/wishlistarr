using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Requests;

namespace Wishlistarr.Api.Tmdb;

public static class TmdbDataExtensions
{
    public static IServiceCollection AddTmdbDataRetention(this IServiceCollection services)
    {
        services.TryAddSingleton<LibraryRefreshGate>();
        services.AddScoped<TmdbRetention>();
        services.AddScoped<TmdbDataService>();
        services.TryAddSingleton<TmdbPurgeRunner>();
        services.AddHostedService<TmdbPurgeHostedService>();
        services.AddScoped<RequestTitles>();
        services.AddScoped<ITmdbDataStore, LibraryTmdbDataStore>();
        services.AddScoped<ITmdbDataStore, RequestTmdbDataStore>();
        services.AddBackgroundJob<TmdbDataPurgeJob>(
            TmdbDataPurgeJob.JobName,
            "Refreshes stored TMDB data that is still in use and purges what is older than the retention.",
            TimeSpan.FromDays(1));
        return services;
    }
}
