using Wishlistarr.Core.Kids;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Wishlistarr.Core.Kometa;

public sealed record KometaProfile(string Name, string PlexLabel);

public static class KometaCompanion
{
    private static readonly HashSet<string> PlainWords = ["collections", "smart_filter", "all", "label", "validate"];

    public static string Generate(IEnumerable<KometaProfile> profiles)
    {
        Dictionary<string, object> collections = [];
        foreach (KometaProfile profile in profiles.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (PlexLabelRules.Validate(profile.PlexLabel) is string error)
            {
                throw new ArgumentException(error, nameof(profiles));
            }

            collections[profile.Name] = new Dictionary<string, object>
            {
                ["smart_filter"] = new Dictionary<string, object>
                {
                    ["all"] = new Dictionary<string, object>
                    {
                        ["label"] = profile.PlexLabel,
                    },
                    ["validate"] = false,
                },
            };
        }

        Dictionary<string, object> root = new()
        {
            ["collections"] = collections,
        };

        ISerializer serializer = new SerializerBuilder()
            .WithEventEmitter(next => new QuotedStringEmitter(next))
            .Build();

        return serializer.Serialize(root);
    }

    private sealed class QuotedStringEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string) && eventInfo.Source.Value is string value && !PlainWords.Contains(value))
            {
                eventInfo.Style = ScalarStyle.DoubleQuoted;
            }

            base.Emit(eventInfo, emitter);
        }
    }
}
