using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Data;

public sealed class LibraryItem
{
    public int Id { get; set; }

    public required string ServerId { get; set; }

    public required string RatingKey { get; set; }

    public required string Guid { get; set; }

    public MediaKind Kind { get; set; }

    public required string LibraryId { get; set; }

    public required string Title { get; set; }

    public int? Year { get; set; }

    public DateTimeOffset? PlexUpdatedAt { get; set; }

    public DateTimeOffset IndexedAt { get; set; }

    public int? TitleId { get; set; }

    public LibraryTitle? CanonicalTitle { get; set; }

    public List<LibraryExternalId> ExternalIds { get; set; } = [];

    public List<LibraryItemLabel> Labels { get; set; } = [];
}

public sealed class LibraryTitle
{
    public int Id { get; set; }

    public MediaKind Kind { get; set; }

    public int TmdbId { get; set; }

    public List<LibraryItem> Items { get; set; } = [];
}

public enum LibraryIdSource
{
    Tmdb,
    Tvdb,
    Imdb,
    PlexGuid,
    PlexRatingKey,
}

public enum LibraryIdOrigin
{
    Plex,
    TmdbLookup,
}

public sealed class LibraryExternalId
{
    public int Id { get; set; }

    public int ItemId { get; set; }

    public LibraryItem? Item { get; set; }

    public LibraryIdSource Source { get; set; }

    public required string Value { get; set; }

    public LibraryIdOrigin Origin { get; set; }

    public DateTimeOffset? FetchedAt { get; set; }
}

public sealed class LibraryItemLabel
{
    public const int MaxLength = 200;

    public int Id { get; set; }

    public int ItemId { get; set; }

    public LibraryItem? Item { get; set; }

    public required string Label { get; set; }
}

public sealed class LibraryRefreshState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string? ServerId { get; set; }

    public DateTimeOffset? LastFullRefreshAt { get; set; }

    public DateTimeOffset? LastIncrementalRefreshAt { get; set; }

    public DateTimeOffset? LastFullAttemptAt { get; set; }

    public bool? LastFullAttemptComplete { get; set; }

    public DateTimeOffset? UpdatedCursor { get; set; }
}
