using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Data;

[TmdbData(nameof(TitleFetchedAt), nameof(Title))]
public sealed class ProfileTitleRule
{
    public int KidProfileId { get; set; }

    public MediaKind Kind { get; set; }

    public int TmdbId { get; set; }

    public bool Blocked { get; set; }

    public string? Title { get; set; }

    public DateTimeOffset? TitleFetchedAt { get; set; }
}
