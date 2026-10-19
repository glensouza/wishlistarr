namespace Wishlistarr.Data;

public sealed class Setting
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public bool IsSecret { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
