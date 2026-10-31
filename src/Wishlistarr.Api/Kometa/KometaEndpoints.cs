using System.Text;
using Wishlistarr.Api.Kids;
using Wishlistarr.Core.Kometa;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Kometa;

public static class KometaEndpoints
{
    public const string DefaultFileName = "wishlistarr-collections.yml";
    public const string YamlContentType = "application/yaml";

    public static void MapKometa(this RouteGroupBuilder v1)
    {
        RouteGroupBuilder kometa = v1.MapGroup("/kometa").WithTags("Kometa");

        kometa.MapGet("/collections.yml", async (KidProfiles profiles, HttpContext http, CancellationToken ct) =>
        {
            IReadOnlyList<KidProfile> active = await profiles.ListAsync(ct);
            string yaml = KometaCompanion.Generate(active.Select(profile => new KometaProfile(profile.Name, profile.PlexLabel)));

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetBytes(yaml), YamlContentType, DefaultFileName);
        })
            .WithName("DownloadKometaCollections")
            .WithSummary("Generates a Kometa collection file mapping active kid profiles to Plex smart label collections.")
            .Produces(StatusCodes.Status200OK, contentType: YamlContentType);
    }
}
