using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Data;

public sealed class ParentNote
{
    public int Id { get; set; }

    public MediaKind Kind { get; set; }

    public int TmdbId { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
