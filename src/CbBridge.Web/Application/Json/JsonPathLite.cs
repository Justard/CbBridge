using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CbBridge.Web.Application.Json;

/// <summary>Small helpers for reading codebeamer JSON without fixed C# models (shape not known yet).</summary>
public static partial class JsonPathLite
{
    /// <summary>"a.b,c" → tries path a.b, then c. Returns the first that exists.</summary>
    public static JsonNode? Get(JsonNode? node, string alternatives)
    {
        if (node is null) return null;
        foreach (var path in alternatives.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? cur = node;
            foreach (var part in path.Split('.'))
            {
                if (cur is JsonObject o && o.TryGetPropertyValue(part, out var next) && next is not null) cur = next;
                else { cur = null; break; }
            }
            if (cur is not null) return cur;
        }
        return null;
    }

    /// <summary>Human-readable text for a table cell.</summary>
    public static string Display(JsonNode? n, int max = 120)
    {
        var s = n switch
        {
            null => "",
            JsonValue v => v.ToString(),
            JsonObject o when o["name"] is JsonValue name => name.ToString(),
            JsonObject o when o["uri"] is JsonValue uri => uri.ToString(),
            JsonArray a => $"[{a.Count}]",
            _ => n.ToJsonString()
        };
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>Numeric id from 1234, "1234", "/item/1234" or {"id":..} / {"uri":"/item/1234"}.</summary>
    public static string? IdOf(JsonNode? n)
    {
        switch (n)
        {
            case JsonValue v:
            {
                var s = v.ToString();
                var m = TrailingDigits().Match(s);
                return m.Success ? m.Groups[1].Value : s;
            }
            case JsonObject o:
                if (o["id"] is JsonValue id) return id.ToString();
                if (o["uri"] is JsonValue uri)
                {
                    var m = TrailingDigits().Match(uri.ToString());
                    if (m.Success) return m.Groups[1].Value;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Turns any list-like response into flat rows. Nested "children" (outlines) are flattened
    /// and get a "_level" column so the tree is still visible.
    /// </summary>
    public static List<JsonObject> Rows(JsonNode? n)
    {
        var rows = new List<JsonObject>();
        switch (n)
        {
            case JsonArray a:
                Flatten(a, 0, rows);
                break;
            case JsonObject o:
                var list = new[] { "items", "children", "outline", "references", "result", "data" }
                    .Select(k => o[k]).OfType<JsonArray>().FirstOrDefault();
                if (list is not null) Flatten(list, 0, rows);
                else rows.Add((JsonObject)o.DeepClone());
                break;
        }
        return rows;
    }

    private static void Flatten(JsonArray a, int level, List<JsonObject> rows)
    {
        foreach (var item in a)
        {
            if (item is not JsonObject o) continue;
            var row = (JsonObject)o.DeepClone();
            var children = row["children"] as JsonArray;
            row.Remove("children");
            if (level > 0 || children is { Count: > 0 }) row["_level"] = level;
            rows.Add(row);
            if (children is not null) Flatten(children, level + 1, rows);
        }
    }

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex TrailingDigits();
}
