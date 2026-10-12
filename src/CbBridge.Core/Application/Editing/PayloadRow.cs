using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Editing;

/// <summary>
/// One test run in the edit table: the extracted original plus the user's edits.
/// Steps in codebeamer's shape (list of positional arrays) are edited in a grid;
/// any other shape falls back to editing the JSON text.
/// </summary>
public sealed class PayloadRow
{
    // Relaxed escaping so "<", ">" and umlauts stay readable in the editor.
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private string _stepsText = "";
    private string _paramsText = "";
    private string _extraText = "";

    public PayloadRow(TestRunPayload original)
    {
        Original = original;
        StepsAsGrid = IsGridShape(original.TestRunSteps);
        Reset();
    }

    public TestRunPayload Original { get; }
    public bool Include { get; set; } = true;

    /// <summary>UI only: editor open (true) or folded to its header line (false).</summary>
    public bool Expanded { get; set; } = true;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int TimeLimitSec { get; set; }

    /// <summary>true when steps are codebeamer-style rows: [[action, expected, critical], ...]</summary>
    public bool StepsAsGrid { get; }

    /// <summary>Grid mode: one list of cells per step, in codebeamer column order.</summary>
    public List<List<JsonNode?>> StepCells { get; private set; } = new();

    /// <summary>JSON mode (unknown step shape).</summary>
    public string StepsText
    {
        get => _stepsText;
        set { _stepsText = value; StepsError = Check(value); }
    }

    public string ParamsText
    {
        get => _paramsText;
        set { _paramsText = value; ParamsError = Check(value); }
    }

    /// <summary>Test Configuration — composed from release + version names, editable as plain text.</summary>
    public string Configuration { get; set; } = "";

    /// <summary>Release, Version and other extra fields, as one JSON object.</summary>
    public string ExtraText
    {
        get => _extraText;
        set
        {
            _extraText = value;
            ExtraError = Check(value) ?? (string.IsNullOrWhiteSpace(value) || Parse(value) is JsonObject ? null : "Must be a JSON object: { \"releases\": [...], ... }");
        }
    }

    public string? StepsError { get; private set; }
    public string? ParamsError { get; private set; }
    public string? ExtraError { get; private set; }
    public bool IsValid => StepsError is null && ParamsError is null && ExtraError is null && TimeLimitSec > 0;

    /// <summary>Releases/versions as currently edited (null while their JSON is invalid).</summary>
    public JsonObject? CurrentExtra => ExtraError is null ? Parse(ExtraText) as JsonObject : null;

    public IEnumerable<(string Key, string Text)> ExtraSummary =>
        ExtraError is null ? PayloadBuilder.ExtraSummary(Parse(ExtraText) as JsonObject) : Enumerable.Empty<(string, string)>();

    public bool IsEdited =>
        Name != Original.Name
        || Description != (Original.Description ?? "")
        || TimeLimitSec != Original.TimeLimitSec
        || Compact(CurrentSteps()) != Compact(Original.TestRunSteps)
        || Normalize(ParamsText) != Normalize(ToText(Original.Parameters))
        || Configuration != (Original.TestConfiguration ?? "")
        || Normalize(ExtraText) != Normalize(ToText(Original.Extra));

    public void Reset()
    {
        Name = Original.Name;
        Description = Original.Description ?? "";
        TimeLimitSec = Original.TimeLimitSec;
        ParamsText = ToText(Original.Parameters);
        Configuration = Original.TestConfiguration ?? "";
        ExtraText = ToText(Original.Extra);
        if (StepsAsGrid)
        {
            StepCells = (Original.TestRunSteps as JsonArray ?? new JsonArray())
                .Select(step => ((JsonArray)step!).Select(c => c?.DeepClone()).ToList())
                .ToList();
        }
        else
        {
            StepsText = ToText(Original.TestRunSteps);
        }
    }

    // ── grid operations ──
    public void AddStep(int columns, Func<int, bool> isBoolColumn) =>
        StepCells.Add(Enumerable.Range(0, columns)
            .Select(c => (JsonNode?)(isBoolColumn(c) ? JsonValue.Create(false) : JsonValue.Create("")))
            .ToList());

    public void RemoveStep(int index) => StepCells.RemoveAt(index);

    public void MoveStep(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= StepCells.Count) return;
        (StepCells[index], StepCells[target]) = (StepCells[target], StepCells[index]);
    }

    public void SetCell(int row, int column, JsonNode? value)
    {
        var cells = StepCells[row];
        while (cells.Count <= column) cells.Add(null);
        cells[column] = value;
    }

    /// <summary>The edited payload exactly as it will be sent. Call only when IsValid.</summary>
    public TestRunPayload ToPayload() => new()
    {
        IsLocal = Original.IsLocal,
        SourceTestCaseId = Original.SourceTestCaseId,
        GroupName = Original.GroupName,
        ItemId = Original.ItemId,
        Parent = Original.Parent,
        Modifier = Original.Modifier,
        Name = Name,
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
        TestRunSteps = CurrentSteps(),
        Parameters = Parse(ParamsText),
        TestConfiguration = string.IsNullOrWhiteSpace(Configuration) ? null : Configuration.Trim(),
        Extra = Parse(ExtraText) as JsonObject,
        TimeLimitSec = TimeLimitSec
    };

    public string ToJson() => ToPayload().ToJson(Pretty);

    private JsonNode? CurrentSteps()
    {
        if (!StepsAsGrid) return StepsError is null ? Parse(StepsText) : null;
        if (Original.TestRunSteps is null && StepCells.Count == 0) return null;
        return new JsonArray(StepCells
            .Select(cells => (JsonNode?)new JsonArray(cells.Select(c => c?.DeepClone()).ToArray()))
            .ToArray());
    }

    private static bool IsGridShape(JsonNode? n) => n is null || (n is JsonArray a && a.All(x => x is JsonArray));

    private static string ToText(JsonNode? n) => n?.ToJsonString(Pretty) ?? "";
    private static string Compact(JsonNode? n) => n?.ToJsonString() ?? "";

    private static JsonNode? Parse(string text) => string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);

    private static string? Check(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { JsonNode.Parse(text); return null; }
        catch (JsonException ex) { return "Not valid JSON: " + ex.Message; }
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        try { return JsonNode.Parse(text)?.ToJsonString() ?? ""; }
        catch (JsonException) { return text; }
    }
}
