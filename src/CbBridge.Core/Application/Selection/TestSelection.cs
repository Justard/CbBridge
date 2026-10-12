using System.Text.Json.Nodes;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Editing;
using CbBridge.Core.Application.Json;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Selection;

public sealed class TestablaufGroup
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public List<TestCaseRef> Cases { get; set; } = new();
    public string? Error { get; set; }
}

public sealed record TestCaseRef(string Id, string Name);

/// <summary>
/// Finding and preparing tests — shared by the browser UI and the CI API, so both select and build
/// test runs exactly the same way.
/// </summary>
public sealed class TestSelection(
    ICodebeamerReader reader,
    PayloadBuilder builder,
    IOptions<RunMultipleOptions> runMultipleOptions)
{
    /// <summary>All Testabläufe (test runs / test sets) in a category, each with its test cases. Folders are skipped.</summary>
    public async Task<List<TestablaufGroup>> LoadTestablaeufeAsync(string categoryId, Func<string, Task>? progress = null)
    {
        var rows = JsonPathLite.Rows(await reader.GetOutlineAsync(categoryId));
        var groups = rows
            .Where(r => JsonPathLite.IdOf(r) is not null)
            .Where(r => !JsonPathLite.Display(r["type"]).Contains("folder", StringComparison.OrdinalIgnoreCase))
            .Select(r => new TestablaufGroup
            {
                Id = JsonPathLite.IdOf(r)!,
                Name = JsonPathLite.Display(r["name"], int.MaxValue),
                Type = JsonPathLite.Display(r["type"], 40)
            })
            .ToList();

        var n = 0;
        foreach (var g in groups)
        {
            n++;
            if (progress is not null) await progress($"Reading test cases of Testablauf {n} of {groups.Count}: {g.Name}…");
            try { g.Cases = await TestCasesOfAsync(g.Id); }
            catch (Exception ex) { g.Error = ex is CodebeamerException ? ex.Message : $"Could not read test cases: {ex.Message}"; }
        }
        return groups;
    }

    /// <summary>One Testablauf with its test cases.</summary>
    public async Task<TestablaufGroup> LoadTestablaufAsync(string testablaufId)
    {
        var item = await reader.GetItemAsync(testablaufId);
        return new TestablaufGroup
        {
            Id = testablaufId,
            Name = JsonPathLite.Display(JsonPathLite.Get(item, "name"), int.MaxValue),
            Type = JsonPathLite.Display(JsonPathLite.Get(item, "type"), 40),
            Cases = await TestCasesOfAsync(testablaufId)
        };
    }

    /// <summary>Test cases of one Testablauf: from a configured field of the item, or from its references.</summary>
    public async Task<List<TestCaseRef>> TestCasesOfAsync(string testablaufId)
    {
        var rm = runMultipleOptions.Value;
        IEnumerable<JsonObject> links;
        if (!string.IsNullOrWhiteSpace(rm.TestCaseField))
        {
            var item = await reader.GetItemAsync(testablaufId);
            links = JsonPathLite.Get(item, rm.TestCaseField) is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
        }
        else
        {
            var types = rm.EffectiveTestCaseTypes;
            links = JsonPathLite.Rows(await reader.GetReferencesAsync(testablaufId))
                .Where(r => r["type"] is null || types.Any(t => JsonPathLite.Display(r["type"]).Contains(t, StringComparison.OrdinalIgnoreCase)));
        }
        return links
            .Select(l => new TestCaseRef(JsonPathLite.IdOf(l) ?? "", JsonPathLite.Display(l["name"], int.MaxValue)))
            .Where(c => c.Id.Length > 0)
            .ToList();
    }

    /// <summary>An existing codebeamer test run, ready to send (results go back to this run).</summary>
    public async Task<TestRunPayload> TestRunPayloadAsync(string itemId, int timeLimitSec)
    {
        var item = await reader.GetItemAsync(itemId) ?? throw new CodebeamerException($"GET /item/{itemId} returned nothing");
        var payload = builder.FromItem(item, timeLimitSec);
        if (string.IsNullOrEmpty(payload.ItemId)) payload.ItemId = itemId;
        return payload;
    }

    /// <summary>
    /// A test case, ready to send. A new test run can be created for it at execution (Run multiple).
    /// releaseVersion (from <see cref="ReleaseVersion"/>) replaces the test case's own releases/versions.
    /// </summary>
    public async Task<TestRunPayload> TestCasePayloadAsync(string testCaseId, TestablaufGroup? group, JsonObject? releaseVersion,
        int timeLimitSec, Dictionary<string, JsonNode>? cache = null)
    {
        JsonNode? item = null;
        if (cache is null || !cache.TryGetValue(testCaseId, out item))
        {
            item = await reader.GetItemAsync(testCaseId) ?? throw new CodebeamerException($"GET /item/{testCaseId} returned nothing");
            if (cache is not null) cache[testCaseId] = item;
        }

        var payload = builder.FromItem(item, timeLimitSec);
        payload.ItemId = testCaseId;
        payload.SourceTestCaseId = testCaseId;
        payload.GroupName = group?.Name;

        var extra = payload.Extra ?? new JsonObject();
        if (releaseVersion is not null)
            foreach (var (key, value) in releaseVersion) extra[key] = value?.DeepClone();
        extra["testCaseId"] = testCaseId;
        if (group is not null) extra["testablauf"] = new JsonObject { ["id"] = group.Id, ["name"] = group.Name };
        payload.Extra = extra;
        payload.TestConfiguration = builder.ComposeConfiguration(extra);
        return payload;
    }

    /// <summary>{"releases":[{name}], "versions":[{name},…]} from plain names; null when both are empty.</summary>
    public static JsonObject? ReleaseVersion(string? release, IEnumerable<string>? versions)
    {
        var extra = new JsonObject();
        if (!string.IsNullOrWhiteSpace(release))
            extra["releases"] = new JsonArray(new JsonObject { ["name"] = release.Trim() });
        var list = (versions ?? Enumerable.Empty<string>()).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        if (list.Count > 0)
            extra["versions"] = new JsonArray(list.Select(v => (JsonNode?)new JsonObject { ["name"] = v }).ToArray());
        return extra.Count > 0 ? extra : null;
    }
}
