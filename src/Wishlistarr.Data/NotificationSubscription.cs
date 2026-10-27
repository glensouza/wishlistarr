namespace Wishlistarr.Data;

public sealed class NotificationSubscription
{
    public int Id { get; set; }

    public int ParentAccountId { get; set; }

    public required string Provider { get; set; }

    public bool Enabled { get; set; }

    public string Events { get; set; } = string.Empty;

    public required string ProtectedConfiguration { get; set; }

    public int LastEventId { get; set; }
}
