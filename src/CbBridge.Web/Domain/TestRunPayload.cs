using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CbBridge.Web.Domain;

/// <summary>
/// The JSON the bridge sends to the Python framework (POST /execute) for ONE test run.
/// Always serialize with <see cref="ToJson"/> / read with <see cref="FromJson"/>, so the extra fields
/// (releases, versions, …) end up as top-level keys.
/// </summary>
public sealed class TestRunPayload
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Kept as raw JSON: the exact shape comes from your codebeamer instance.</summary>
    [JsonPropertyName("testRunSteps")] public JsonNode? TestRunSteps { get; set; }

    /// <summary>Kept as raw JSON: object or list, whatever codebeamer returns.</summary>
    [JsonPropertyName("parameters")] public JsonNode? Parameters { get; set; }

    [JsonPropertyName("modifier")] public string? Modifier { get; set; }

    /// <summary>
    /// Test Configuration — not a codebeamer field: composed from the names in the Release and
    /// Version fields (template in Codebeamer:FieldMap:TestConfigurationTemplate), editable before sending.
    /// </summary>
    [JsonPropertyName("testConfiguration")] public string? TestConfiguration { get; set; }

    /// <summary>The framework must stop the test itself when this runs out and still return a result.</summary>
    [JsonPropertyName("timeLimitSec")] public int TimeLimitSec { get; set; }

    /// <summary>
    /// Further codebeamer fields configured in Codebeamer:FieldMap:ExtraFields (default: releases, versions).
    /// Written as top-level keys next to the fields above by <see cref="ToJson"/>.
    /// (Not [JsonExtensionData]: .NET 8 nests a JsonObject there instead of merging its keys.)
    /// </summary>
    [JsonIgnore] public JsonObject? Extra { get; set; }

    /// <summary>Created in the bridge ("My tests"), not extracted from codebeamer — never posted back.</summary>
    [JsonIgnore] public bool IsLocal { get; set; }

    public static string NewLocalId() => "local-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>The framework request body: fixed fields, then each extra field as its own top-level key.</summary>
    public JsonObject ToJsonObject()
    {
        var json = JsonSerializer.SerializeToNode(this)!.AsObject();
        if (Extra is not null)
            foreach (var (key, value) in Extra)
                if (!json.ContainsKey(key)) json[key] = value?.DeepClone();
        return json;
    }

    public string ToJson(JsonSerializerOptions? options = null) => ToJsonObject().ToJsonString(options);

    /// <summary>Reads a request body back (e.g. an imported file); unknown keys become extra fields.</summary>
    public static TestRunPayload FromJson(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("The file must contain a JSON object.");
        var payload = node.Deserialize<TestRunPayload>() ?? new TestRunPayload();
        var known = typeof(TestRunPayload).GetProperties()
            .Select(p => p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name)
            .Where(n => n is not null)
            .ToHashSet();
        var extra = new JsonObject();
        foreach (var (key, value) in node)
            if (!known.Contains(key)) extra[key] = value?.DeepClone();
        payload.Extra = extra.Count > 0 ? extra : null;
        return payload;
    }

    public TestRunPayload WithTimeLimit(int seconds) => new()
    {
        IsLocal = IsLocal,
        ItemId = ItemId,
        Name = Name,
        Parent = Parent,
        Description = Description,
        TestRunSteps = TestRunSteps?.DeepClone(),
        Parameters = Parameters?.DeepClone(),
        Modifier = Modifier,
        TestConfiguration = TestConfiguration,
        Extra = Extra?.DeepClone() as JsonObject,
        TimeLimitSec = seconds
    };
}
