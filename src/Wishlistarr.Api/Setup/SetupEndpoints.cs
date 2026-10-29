using System.Text.Json;
using System.Xml;
using Wishlistarr.Api.Fulfilment;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Arr;
using Wishlistarr.Integrations.Seerr;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Setup;

public static class SetupEndpoints
{
    public const string CompletedKey = "system.setup_completed";
    public const string DismissedKey = "system.setup_dismissed";

    public static void MapSetup(this RouteGroupBuilder parent)
    {
        RouteGroupBuilder setup = parent.MapGroup("/setup").WithTags("Setup");
        setup.MapGet("/", StatusAsync)
            .WithName("GetSetupStatus")
            .WithSummary("Reports whether the guided setup has been completed and whether it should open on its own. It opens only until it is completed or exited once, and never when Plex, TMDB and a request service are already saved.")
            .Produces<SetupStatus>();

        setup.MapPost("/dismiss", async (SettingsStore store, CancellationToken ct) =>
        {
            await store.SetAsync(DismissedKey, "true", false, ct);
            return Results.NoContent();
        })
            .WithName("DismissSetup")
            .WithSummary("Stops the guided setup from opening on its own. It can still be run again from Settings.")
            .Produces(StatusCodes.Status204NoContent);

        setup.MapPost("/complete", CompleteAsync)
            .WithName("CompleteSetup")
            .WithSummary("Rechecks the saved connections, request options and selected kid's Plex restrictions before completing setup.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }

    private static async Task<SetupStatus> StatusAsync(
        SettingsStore store,
        IMediaServerAccount plex,
        FulfilmentMode mode,
        SeerrConnectionStore seerr,
        ArrConnectionStore arr,
        CancellationToken ct)
    {
        bool completed = await store.GetAsync(CompletedKey, ct) == "true";
        bool dismissed = await store.GetAsync(DismissedKey, ct) == "true";
        bool showGuide = !completed && !dismissed && !await ConnectionsSavedAsync(store, plex, mode, seerr, arr, ct);
        return new SetupStatus(completed, showGuide);
    }

    private static async Task<bool> ConnectionsSavedAsync(
        SettingsStore store,
        IMediaServerAccount plex,
        FulfilmentMode mode,
        SeerrConnectionStore seerr,
        ArrConnectionStore arr,
        CancellationToken ct)
    {
        MediaServerAccountStatus account = await plex.GetStatusAsync(ct);
        bool plexSaved = account.Linked && account.Server is not null;
        bool tmdbSaved = !string.IsNullOrEmpty(await store.GetAsync(TmdbOptions.ApiKeySetting, ct));
        if (!plexSaved || !tmdbSaved)
        {
            return false;
        }

        return await mode.GetAsync(ct) switch
        {
            FulfilmentBackend.Seerr => await seerr.GetAsync(ct) is not null,
            FulfilmentBackend.Arr => await arr.GetAsync(ArrService.Sonarr, ct) is not null && await arr.GetAsync(ArrService.Radarr, ct) is not null,
            _ => false,
        };
    }

    private static async Task<IResult> CompleteAsync(
        SetupComplete? input,
        SettingsStore store,
        IMediaServer plex,
        ITmdbClient tmdb,
        FulfilmentMode mode,
        ISeerrBackend seerr,
        ArrBackends arr,
        ArrConnectionStore connections,
        KidProfiles profiles,
        PlexEnforcement enforcement,
        CancellationToken ct)
    {
        string step = "Plex";
        try
        {
            MediaServerTestResult plexTest = await plex.TestConnectionAsync(ct);
            if (!plexTest.Valid)
            {
                return Incomplete(plexTest.Message);
            }

            step = "TMDB";
            if (!await tmdb.TestApiKeyAsync(ct))
            {
                return Incomplete("TMDB rejected the API key. Check the TMDB step.");
            }

            step = "Request service";
            switch (await mode.GetAsync(ct))
            {
                case FulfilmentBackend.Seerr:
                    await seerr.TestAsync(ct);
                    break;
                case FulfilmentBackend.Arr:
                    foreach (ArrService service in Enum.GetValues<ArrService>())
                    {
                        IArrBackend backend = arr.Get(service);
                        ArrTestResult test = await backend.TestAsync(ct);
                        if (!test.Valid)
                        {
                            return Incomplete(test.Message);
                        }

                        ArrSettingsView settings = await connections.GetViewAsync(service, ct);
                        IReadOnlyList<ArrQualityProfile> qualities = await backend.GetQualityProfilesAsync(ct);
                        IReadOnlyList<ArrRootFolder> folders = await backend.GetRootFoldersAsync(ct);
                        if (!qualities.Any(profile => profile.Id == settings.QualityProfileId)
                            || !folders.Any(folder => folder.Path == settings.RootFolderPath))
                        {
                            return Incomplete($"Choose an available quality profile and root folder for {service}.");
                        }
                    }

                    break;
                default:
                    return Incomplete("Choose Seerr or Sonarr and Radarr in the request service step.");
            }

            step = "Plex restrictions";
            KidProfile? kid = (await profiles.ListAsync(ct)).FirstOrDefault(profile => profile.Id == input?.ProfileId);
            if (kid is null || string.IsNullOrWhiteSpace(kid.PlexUserId))
            {
                return Incomplete("Choose a saved kid profile and its Plex user before finishing setup.");
            }

            ProfileRestrictions? restrictions = (await enforcement.CheckAsync([kid], ct)).GetValueOrDefault(kid.Id);
            bool moviesRestricted = restrictions?.Movies.Restricted == true;
            bool televisionRestricted = restrictions?.Television.Restricted == true;
            if (!moviesRestricted || !televisionRestricted)
            {
                return Incomplete("Plex restrictions are not confirmed for both movies and TV. For each, allow only some content ratings, this profile's label, or both, then check the restrictions step and try again.");
            }

            await store.SetAsync(CompletedKey, "true", false, ct);
            return Results.NoContent();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is MediaServerException or TmdbException or SeerrException or ArrException or HttpRequestException or TaskCanceledException
                or JsonException or XmlException or FormatException or InvalidOperationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: $"{step} could not be checked. Check that step and try again.");
        }
    }

    private static IResult Incomplete(string title) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title);
}

public sealed record SetupStatus(bool Completed, bool ShowGuide);

public sealed record SetupComplete(int ProfileId);
