using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CbBridge.Core.Application.Json;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Editing;

/// <summary>codebeamer item JSON → the framework payload. Field locations come from config.</summary>
public sealed partial class PayloadBuilder(IOptions<CodebeamerOptions> options)
{
    public TestRunPayload FromItem(JsonNode item, int defaultTimeLimitSec)
    {
        var map = options.Value.FieldMap;
        var parent = JsonPathLite.Get(item, map.Parent);
        var extra = ExtraFrom(item, map);
        return new TestRunPayload
        {
            ItemId = JsonPathLite.IdOf(JsonPathLite.Get(item, map.ItemId)) ?? "",
            Name = JsonPathLite.Display(JsonPathLite.Get(item, map.Name), int.MaxValue),
            Parent = JsonPathLite.IdOf(parent) ?? NullIfEmpty(JsonPathLite.Display(parent, int.MaxValue)),
            Description = NullIfEmpty(JsonPathLite.Display(JsonPathLite.Get(item, map.Description), int.MaxValue)),
            TestRunSteps = JsonPathLite.Get(item, map.TestRunSteps)?.DeepClone(),
            Parameters = JsonPathLite.Get(item, map.Parameters)?.DeepClone(),
            Modifier = NullIfEmpty(JsonPathLite.Display(JsonPathLite.Get(item, map.Modifier), int.MaxValue)),
            Extra = extra,
            TestConfiguration = ComposeConfiguration(extra),
            TimeLimitSec = defaultTimeLimitSec
        };
    }

    /// <summary>
    /// Test Configuration = template with {key} replaced by the names in that extra field,
    /// e.g. "{releases} / {versions}" → "R4.3 / FW 4.3.1, HW C2". Empty parts are dropped.
    /// </summary>
    public string? ComposeConfiguration(JsonObject? extra)
    {
        var map = options.Value.FieldMap;
        var text = PlaceholderRx().Replace(map.TestConfigurationTemplate, m =>
            string.Join(map.TestConfigurationNameSeparator, Names(extra?[m.Groups[1].Value])));

        // drop separators left behind by empty parts: "R4.3 / " → "R4.3", " /  / FW" → "FW"
        text = RepeatedSeparatorRx().Replace(text, m => m.Groups[1].Value);
        text = text.Trim().Trim('/', ',', ';', '|', '·', '-').Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>Names inside a field value: [{"name":"R4.3"}, …] → R4.3, …; plain strings as they are.</summary>
    public static IEnumerable<string> Names(JsonNode? value) => value switch
    {
        null => Enumerable.Empty<string>(),
        JsonArray a => a.Select(x => JsonPathLite.Display(x, int.MaxValue)).Where(s => s.Length > 0),
        _ => new[] { JsonPathLite.Display(value, int.MaxValue) }.Where(s => s.Length > 0)
    };

    private static JsonObject? ExtraFrom(JsonNode item, FieldMapOptions map)
    {
        var extra = new JsonObject();
        foreach (var (key, path) in map.EffectiveExtraFields)
        {
            var value = JsonPathLite.Get(item, path);
            if (value is not null) extra[key] = value.DeepClone();
        }
        return extra.Count > 0 ? extra : null;
    }

    /// <summary>Short labels per extra field, e.g. ("releases", "R4.3 · R4.4").</summary>
    public static IEnumerable<(string Key, string Text)> ExtraSummary(JsonObject? extra) =>
        extra is null
            ? Enumerable.Empty<(string, string)>()
            : extra.Select(kv => (kv.Key, string.Join(" · ", Names(kv.Value)))).Where(x => x.Item2.Length > 0);

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")] private static partial Regex PlaceholderRx();
    // two or more separators with only spaces between them collapse into the first one
    [GeneratedRegex(@"(\s*[/,;|·-]\s*)(?:[/,;|·-]\s*)+")] private static partial Regex RepeatedSeparatorRx();
}
