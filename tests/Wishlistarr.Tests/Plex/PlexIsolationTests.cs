using System.Reflection;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexIsolationTests
{
    private const string PlexNamespace = "Wishlistarr.Integrations.Plex";

    private const BindingFlags Members =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Integrations_exports_only_registration_and_options_for_plex()
    {
        string[] exported =
        [
            .. typeof(PlexOptions).Assembly.GetExportedTypes()
                .Where(type => type.Namespace == PlexNamespace)
                .Select(type => type.Name)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(["PlexOptions", "PlexServiceCollectionExtensions"], exported);
    }

    [Fact]
    public void Core_and_api_types_never_mention_plex_types()
    {
        List<string> leaks = [];
        foreach (Type type in typeof(IMediaServer).Assembly.GetTypes().Concat(typeof(SettingsStore).Assembly.GetTypes()))
        {
            IEnumerable<Type> used = type.GetFields(Members).Select(field => field.FieldType)
                .Concat(type.GetProperties(Members).Select(property => property.PropertyType))
                .Concat(type.GetMethods(Members).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
                .Concat(type.GetConstructors(Members).SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)));
            leaks.AddRange(used.SelectMany(Flatten).Where(IsPlexType).Select(leak => $"{type.FullName} uses {leak.FullName}"));
        }

        Assert.Empty(leaks);
    }

    private static bool IsPlexType(Type type) => type.Namespace == PlexNamespace;

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        Type? element = type.HasElementType ? type.GetElementType() : null;
        if (element is not null)
        {
            foreach (Type inner in Flatten(element))
            {
                yield return inner;
            }
        }

        if (type.IsGenericType)
        {
            foreach (Type inner in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return inner;
            }
        }
    }
}
