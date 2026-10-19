namespace Wishlistarr.Data;

public sealed class ParentAccount
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockoutEndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<ParentSession> Sessions { get; } = [];
}
