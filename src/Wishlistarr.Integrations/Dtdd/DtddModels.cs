using System.Text.Json.Serialization;

namespace Wishlistarr.Integrations.Dtdd;

public sealed record DtddListItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("itemTypeId")] int? ItemTypeId,
    [property: JsonPropertyName("itemTypeName")] string? ItemTypeName,
    [property: JsonPropertyName("tmdbId")] int? TmdbId);

public sealed record DtddItemDetail(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("topicItemStats")] IReadOnlyList<DtddTopicStat>? TopicItemStats);

public sealed record DtddTopicStat(
    [property: JsonPropertyName("topicId")] int TopicId,
    [property: JsonPropertyName("topicName")] string? TopicName,
    [property: JsonPropertyName("yesSum")] int? YesSum,
    [property: JsonPropertyName("noSum")] int? NoSum,
    [property: JsonPropertyName("numComments")] int? NumComments,
    [property: JsonPropertyName("itemId")] int? ItemId);

public sealed record DtddError(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("message")] string? Message);
