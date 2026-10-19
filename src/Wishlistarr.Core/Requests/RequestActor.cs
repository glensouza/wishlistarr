namespace Wishlistarr.Core.Requests;

public enum RequestActorKind
{
    Kid,
    Parent,
    System,
}

public sealed record RequestActor(RequestActorKind Kind, int? Id, string Name)
{
    public const int MaxNameLength = 100;

    public static RequestActor Kid(int kidProfileId, string name) => new(RequestActorKind.Kid, kidProfileId, name);

    public static RequestActor Parent(int? parentAccountId, string name) => new(RequestActorKind.Parent, parentAccountId, name);

    public static RequestActor System(string component) => new(RequestActorKind.System, null, component);
}
