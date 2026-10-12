namespace CbBridge.Web.Application;

/// <summary>appsettings.json → "Codebeamer"</summary>
public sealed class CodebeamerOptions
{
    /// <summary>Pre-filled on the login page, e.g. https://cb.yourcompany.com/cb</summary>
    public string DefaultBaseUrl { get; set; } = "";
    /// <summary>Legacy REST — used for all reading.</summary>
    public string LegacyPrefix { get; set; } = "/rest";
    /// <summary>Swagger REST v3 — used for uploading results.</summary>
    public string V3Prefix { get; set; } = "/api/v3";
    /// <summary>Legacy call used to check the login works.</summary>
    public string PingPath { get; set; } = "/projects";
    public FieldMapOptions FieldMap { get; set; } = new();

    /// <summary>
    /// Column titles of the steps table. codebeamer stores each step as a positional array,
    /// e.g. ["Action", "Expected result", true]. A title ending in "?" is edited as a checkbox.
    /// Test run steps may have more columns (actual result, result) — add their titles here.
    /// </summary>
    // Left empty here on purpose: the config binder APPENDS to a pre-filled array instead of replacing it.
    public string[] StepColumns { get; set; } = Array.Empty<string>();

    public string[] EffectiveStepColumns =>
        StepColumns.Length > 0 ? StepColumns : new[] { "Action", "Expected result", "Critical?" };
}

/// <summary>
/// Where each payload field is found in the codebeamer item JSON.
/// Comma = alternatives tried in order; dot = nested ("parent.id").
/// Adjust these once you have seen your real /rest/item/{id} response.
/// </summary>
public sealed class FieldMapOptions
{
    public string ItemId { get; set; } = "id,uri";
    public string Name { get; set; } = "name";
    public string Parent { get; set; } = "parent";
    public string Description { get; set; } = "description";
    public string TestRunSteps { get; set; } = "testRunSteps,testSteps";
    public string Parameters { get; set; } = "parameters,testParameters";
    public string Modifier { get; set; } = "modifier";
    /// <summary>
    /// How Test Configuration is built from the extra fields: {key} is replaced by the names in that
    /// field (several names joined with TestConfigurationNameSeparator). Empty parts are dropped.
    /// Example result: "R4.3 / FW 4.3.1, HW C2".
    /// </summary>
    public string TestConfigurationTemplate { get; set; } = "{releases} / {versions}";
    public string TestConfigurationNameSeparator { get; set; } = ", ";

    /// <summary>
    /// More fields to pass on: payload key → where to find it in the item JSON (comma = alternatives).
    /// Default: releases and versions. Add e.g. "preAction": "preAction" without code changes.
    /// </summary>
    public Dictionary<string, string> ExtraFields { get; set; } = new();

    // Defaults kept here (not pre-filled above) because the config binder merges into pre-filled dictionaries.
    public IReadOnlyDictionary<string, string> EffectiveExtraFields => ExtraFields.Count > 0
        ? ExtraFields
        : new Dictionary<string, string> { ["releases"] = "releases,release", ["versions"] = "versions,version" };

}

/// <summary>appsettings.json → "Framework" (your Python Flask service)</summary>
public sealed class FrameworkOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5001";
    public string ExecutePath { get; set; } = "/execute";
    public string CancelPath { get; set; } = "/cancel/{itemId}";
    /// <summary>Used by "Test connection". A 404 still counts as reachable (framework has no health route).</summary>
    public string HealthPath { get; set; } = "/health";
    /// <summary>HTTP wait = timeLimitSec + this. Only matters if the framework itself freezes.</summary>
    public int GraceSeconds { get; set; } = 15;
    /// <summary>Field(s) in the result JSON that hold the verdict.</summary>
    public string VerdictFields { get; set; } = "verdict,result,status";
    public string MessageFields { get; set; } = "message,error";
}

/// <summary>appsettings.json → "PostBack"</summary>
public sealed class PostBackOptions
{
    /// <summary>true = log the upload request instead of sending it. Safe default for a real server.</summary>
    public bool DryRun { get; set; } = true;
}
