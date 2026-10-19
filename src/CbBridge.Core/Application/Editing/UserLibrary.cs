using System.Text.Json.Serialization;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Editing;

/// <summary>A test the user wrote in the bridge ("My tests").</summary>
public sealed class SavedTest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }

    /// <summary>The full framework JSON (stored as text so extra fields like releases/versions survive).</summary>
    public string PayloadJson { get; set; } = "";

    [JsonIgnore]
    public TestRunPayload? Payload
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PayloadJson)) return null;
            try
            {
                var payload = TestRunPayload.FromJson(PayloadJson);
                payload.IsLocal = true;
                return payload;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
    }

    public static SavedTest From(TestRunPayload payload) => new()
    {
        Id = payload.ItemId,
        Name = payload.Name,
        SavedAt = DateTime.UtcNow,
        PayloadJson = payload.ToJson()
    };
}

/// <summary>A named framework address, e.g. "Bench A" → http://10.0.0.12:5001.</summary>
public sealed class SavedTestSystem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>
/// A saved run setup ("Nightly Bluetooth on Bench A"): the run list as it was (including edits), the run options
/// and the test system. Kept in the user's browser.
/// </summary>
public sealed class RunPreset
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public string FrameworkUrl { get; set; } = "";
    public RunOptions Options { get; set; } = new();
    public List<PresetTest> Tests { get; set; } = new();
}

public sealed class PresetTest
{
    public string PayloadJson { get; set; } = "";
    public bool IsLocal { get; set; }
    public string? SourceTestCaseId { get; set; }
    public string? GroupName { get; set; }
    public bool Include { get; set; } = true;
    public bool Analyze { get; set; } = true;

    public static PresetTest From(TestRunPayload p, bool include) => new()
    {
        PayloadJson = p.ToJson(), IsLocal = p.IsLocal, SourceTestCaseId = p.SourceTestCaseId, GroupName = p.GroupName,
        Include = include, Analyze = p.Analyze
    };

    public TestRunPayload? ToPayload()
    {
        try
        {
            var p = TestRunPayload.FromJson(PayloadJson);
            p.IsLocal = IsLocal;
            p.SourceTestCaseId = SourceTestCaseId;
            p.GroupName = GroupName;
            p.Analyze = Analyze;
            return p;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
