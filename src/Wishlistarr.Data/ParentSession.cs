namespace Wishlistarr.Data;

public sealed class ParentSession
{
    public required string Id { get; set; }

    public int ParentAccountId { get; set; }

    public ParentAccount? ParentAccount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? UserAgent { get; set; }

    public string? RemoteAddress { get; set; }
}
