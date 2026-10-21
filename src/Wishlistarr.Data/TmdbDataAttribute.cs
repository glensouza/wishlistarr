namespace Wishlistarr.Data;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class TmdbDataAttribute(string fetchedAtProperty, params string[] properties) : Attribute
{
    public string FetchedAtProperty { get; } = fetchedAtProperty;

    public IReadOnlyList<string> Properties { get; } = properties;
}
