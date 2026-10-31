using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Kometa;
using Wishlistarr.Core.Kometa;
using Wishlistarr.Data;
using YamlDotNet.Serialization;

namespace Wishlistarr.Tests.Kometa;

public sealed class KometaCompanionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string DownloadPath = "/api/v1/kometa/collections.yml";

    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        string schemaPath = Path.Combine(AppContext.BaseDirectory, "Kometa", "collection-schema.json");
        return JsonSchema.FromFile(schemaPath);
    });

    [Fact]
    public void Schema_validation_passes_for_generated_collection_file()
    {
        List<KometaProfile> profiles =
        [
            new("Ada", "wishlistarr-ada"),
            new("Beau", "wishlistarr-beau"),
        ];

        string yaml = KometaCompanion.Generate(profiles);
        JsonNode node = YamlToJsonNode(yaml);
        EvaluationResults result = Schema.Value.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Empty_profile_list_produces_valid_yaml_and_passes_schema_validation()
    {
        string yaml = KometaCompanion.Generate([]);
        Assert.Contains("collections: {}", yaml, StringComparison.Ordinal);

        JsonNode node = YamlToJsonNode(yaml);
        EvaluationResults result = Schema.Value.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void One_collection_per_profile_with_the_right_label_and_stable_ordering()
    {
        List<KometaProfile> profiles =
        [
            new("Zoe", "label-zoe"),
            new("Alex", "label-alex"),
            new("Maya", "label-maya"),
        ];

        string yaml = KometaCompanion.Generate(profiles);
        IDeserializer deserializer = new DeserializerBuilder().Build();
        Dictionary<string, object> root = deserializer.Deserialize<Dictionary<string, object>>(yaml);
        Dictionary<object, object> collections = (Dictionary<object, object>)root["collections"];

        Assert.Equal(3, collections.Count);
        Assert.Equal(["Alex", "Maya", "Zoe"], collections.Keys.Cast<string>());

        foreach (KometaProfile profile in profiles)
        {
            Dictionary<object, object> collection = (Dictionary<object, object>)collections[profile.Name];
            Dictionary<object, object> smartFilter = (Dictionary<object, object>)collection["smart_filter"];
            Dictionary<object, object> all = (Dictionary<object, object>)smartFilter["all"];
            Assert.Equal(profile.PlexLabel, (string)all["label"]);
        }
    }

    [Fact]
    public void Tricky_labels_round_trip_through_a_yaml_parser_unchanged()
    {
        List<string> tricky =
        [
            "yes", "no", "true", "false", "null", "on", "off",
            ":colon", "hash#tag", "\"quoted\"", "'single'",
            "-leading-dash", "&anchor", "*alias", "!tag", "{brace}",
            "12345", "3.14", "üñîçødë", "日本語", "🦊",
        ];

        List<KometaProfile> profiles = [.. tricky.Select((label, index) => new KometaProfile($"Profile{index}", label))];
        string yaml = KometaCompanion.Generate(profiles);

        IDeserializer deserializer = new DeserializerBuilder().Build();
        Dictionary<string, object> root = deserializer.Deserialize<Dictionary<string, object>>(yaml);
        Dictionary<object, object> collections = (Dictionary<object, object>)root["collections"];

        foreach (KometaProfile profile in profiles)
        {
            Dictionary<object, object> collection = (Dictionary<object, object>)collections[profile.Name];
            Dictionary<object, object> smartFilter = (Dictionary<object, object>)collection["smart_filter"];
            Dictionary<object, object> all = (Dictionary<object, object>)smartFilter["all"];
            Assert.Equal(profile.PlexLabel, (string)all["label"]);
        }

        JsonNode node = YamlToJsonNode(yaml);
        EvaluationResults result = Schema.Value.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Endpoint_returns_yaml_file_with_expected_headers_for_active_profiles_only()
    {
        await factory.EnsureParentAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        string suffix = Guid.NewGuid().ToString("N")[..6];
        string activeName = $"Active-{suffix}";
        string activeLabel = $"label-active-{suffix}";
        string archivedName = $"Archived-{suffix}";
        string archivedLabel = $"label-archived-{suffix}";

        HttpResponseMessage createdActive = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = activeName, avatar = "bear", plexLabel = activeLabel });
        Assert.Equal(HttpStatusCode.Created, createdActive.StatusCode);

        HttpResponseMessage createdArchived = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = archivedName, avatar = "fox", plexLabel = archivedLabel });
        Assert.Equal(HttpStatusCode.Created, createdArchived.StatusCode);
        int archivedId = (await createdArchived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        HttpResponseMessage archivedResponse = await parent.DeleteAsync($"/api/v1/kid-profiles/{archivedId}?removeOwnedLabels=false");
        Assert.Equal(HttpStatusCode.NoContent, archivedResponse.StatusCode);

        HttpResponseMessage response = await parent.GetAsync(DownloadPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(KometaEndpoints.YamlContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(KometaEndpoints.DefaultFileName, response.Content.Headers.ContentDisposition?.FileName);
        Assert.True(response.Headers.CacheControl?.NoStore);

        string yaml = await response.Content.ReadAsStringAsync();
        IDeserializer deserializer = new DeserializerBuilder().Build();
        Dictionary<string, object> root = deserializer.Deserialize<Dictionary<string, object>>(yaml);
        Dictionary<object, object> collections = (Dictionary<object, object>)root["collections"];

        Assert.True(collections.ContainsKey(activeName));
        Assert.False(collections.ContainsKey(archivedName));

        Dictionary<object, object> activeCollection = (Dictionary<object, object>)collections[activeName];
        Dictionary<object, object> smartFilter = (Dictionary<object, object>)activeCollection["smart_filter"];
        Dictionary<object, object> all = (Dictionary<object, object>)smartFilter["all"];
        Assert.Equal(activeLabel, (string)all["label"]);

        JsonNode node = YamlToJsonNode(yaml);
        EvaluationResults schemaResult = Schema.Value.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(schemaResult.IsValid);
    }

    [Fact]
    public async Task Endpoint_authorization_allows_parent_and_denies_kid_and_anonymous()
    {
        await factory.EnsureParentAsync();
        HttpClient anonymous = factory.CreateClient();
        HttpResponseMessage asAnonymous = await anonymous.GetAsync(DownloadPath);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);

        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        HttpResponseMessage asKid = await kid.GetAsync(DownloadPath);
        Assert.Equal(HttpStatusCode.Forbidden, asKid.StatusCode);

        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpResponseMessage asParent = await parent.GetAsync(DownloadPath);
        Assert.Equal(HttpStatusCode.OK, asParent.StatusCode);
    }

    private static JsonNode YamlToJsonNode(string yaml)
    {
        IDeserializer deserializer = new DeserializerBuilder().Build();
        object? yamlObject = deserializer.Deserialize(new StringReader(yaml));
        ISerializer jsonSerializer = new SerializerBuilder().JsonCompatible().Build();
        string json = jsonSerializer.Serialize(yamlObject);
        return JsonNode.Parse(json)!;
    }
}
