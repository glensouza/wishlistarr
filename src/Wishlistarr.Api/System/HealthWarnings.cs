using Wishlistarr.Api.Kids;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.System;

public sealed record HealthWarning(string Code, string Message, int? ProfileId);

public static class HealthWarnings
{
    public const string UnenforcedLabel = "plex.label.not_enforced";

    public static async Task<IReadOnlyList<HealthWarning>> GetAsync(KidProfiles profiles, PlexEnforcement enforcement, CancellationToken ct)
    {
        IReadOnlyList<KidProfile> active = await profiles.ListAsync(ct);
        IReadOnlyDictionary<int, ProfileRestrictions> checks = await enforcement.CheckAsync(active, ct);
        return
        [
            .. active
                .Where(profile => checks.TryGetValue(profile.Id, out ProfileRestrictions? found) && Describe(found) is not null)
                .Select(profile => new HealthWarning(UnenforcedLabel, Message(profile, Describe(checks[profile.Id])!), profile.Id)),
        ];
    }

    private static string? Describe(ProfileRestrictions restrictions)
    {
        bool movies = restrictions.Movies.State == LabelEnforcement.NotEnforced;
        bool television = restrictions.Television.State == LabelEnforcement.NotEnforced;
        return (movies, television) switch
        {
            (true, true) => "movies and TV shows",
            (true, false) => "movies",
            (false, true) => "TV shows",
            _ => null,
        };
    }

    private static string Message(KidProfile profile, string what) =>
        $"The Plex user mapped to {profile.Name} does not filter {what} by the label '{profile.PlexLabel}'. "
        + "In Plex, open Settings, Users and Sharing, edit the user, choose Restrictions and add the label under Allow only or Exclude for each type.";
}
