using System.Globalization;
using Wishlistarr.Core.Kids;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Wishlistarr.Core.Kometa;

public sealed record KometaProfile(string Name, string PlexLabel);

public static class KometaCompanion
{
    private static readonly string[] BooleanAndNullLiterals =
    [
        "yes", "no", "y", "n", "true", "false", "on", "off", "null", "~",
    ];

    private static readonly char[] YamlSpecialCharacters =
    [
        ':', '#', '"', '\'', '{', '}', '[', ']', ',', '&', '*', '?', '|', '-', '<', '>', '=', '!', '%', '@', '`',
    ];

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
                },
            };
        }

        Dictionary<string, object> root = new()
        {
            ["collections"] = collections,
        };

        ISerializer serializer = new SerializerBuilder()
            .WithEventEmitter(next => new YamlQuotingEventEmitter(next))
            .Build();

        return serializer.Serialize(root);
    }

    private sealed class YamlQuotingEventEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string)
                && eventInfo.Source.Value is string value
                && ShouldQuote(value))
            {
                eventInfo.Style = ScalarStyle.DoubleQuoted;
            }

            base.Emit(eventInfo, emitter);
        }

        private static bool ShouldQuote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            foreach (string literal in BooleanAndNullLiterals)
            {
                if (string.Equals(value, literal, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }

            return value.IndexOfAny(YamlSpecialCharacters) >= 0;
        }
    }
}
