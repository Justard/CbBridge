namespace CbBridge.Core.Application;

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
    /// <summary>Known test systems, shown on the Benches page and as suggestions: [{ "Name": "Bench A", "Url": "http://10.0.0.12:5001" }].</summary>
    public List<Editing.SavedTestSystem> Benches { get; set; } = new();

    /// <summary>Contract 1.1 (optional): live step progress of a running test.</summary>
    public string ProgressPath { get; set; } = "/progress/{itemId}";
    /// <summary>How often the bridge asks for progress while a test runs (0 = never).</summary>
    public double ProgressPollSeconds { get; set; } = 1;
    /// <summary>Contract 1.1 (optional): field of the result with files for codebeamer [{name, contentType, contentBase64}].</summary>
    public string AttachmentsField { get; set; } = "attachments";
    /// <summary>Attachments larger than this are not uploaded (logged instead).</summary>
    public int MaxAttachmentMb { get; set; } = 20;
    /// <summary>HTTP wait = timeLimitSec + this. Only matters if the framework itself freezes.</summary>
    public int GraceSeconds { get; set; } = 15;
    /// <summary>Field(s) in the result JSON that hold the verdict.</summary>
    public string VerdictFields { get; set; } = "verdict,result,status";
    public string MessageFields { get; set; } = "message,error";
}

/// <summary>
/// appsettings.json → "RunMultiple": run test cases grouped under Testabläufe (test runs / test sets)
/// of one category, creating a new codebeamer test run per test case.
/// </summary>
public sealed class RunMultipleOptions
{
    /// <summary>Test run tracker/category where new test runs are created (its ID).</summary>
    public string TestRunTrackerId { get; set; } = "";

    /// <summary>Swagger v3 call that creates a test run; {trackerId} is replaced. Align with your instance's Swagger.</summary>
    public string CreatePath { get; set; } = "/trackers/{trackerId}/testruns";

    /// <summary>
    /// Where a Testablauf's test cases come from: empty = its references (GET /item/{id}/references),
    /// or a field of the Testablauf item, e.g. "testCases".
    /// </summary>
    public string TestCaseField { get; set; } = "";

    /// <summary>References whose type contains one of these words count as test cases (empty = all references).</summary>
    public string[] TestCaseTypes { get; set; } = Array.Empty<string>();
    public string[] EffectiveTestCaseTypes => TestCaseTypes.Length > 0 ? TestCaseTypes : new[] { "Test Case", "Testfall" };

    /// <summary>Name of a new test run; {name} = test case name, {testConfiguration}, {testablauf}.</summary>
    public string NewRunNameTemplate { get; set; } = "{name} · {testConfiguration}";
}

/// <summary>appsettings.json → "PostBack"</summary>
public sealed class PostBackOptions
{
    /// <summary>true = log the upload request instead of sending it. Safe default for a real server.</summary>
    public bool DryRun { get; set; } = true;
}

/// <summary>
/// appsettings.json → "Analysis": failed tests are sent to an AI agent (n8n, Zapier, Make, a Python service …)
/// over a plain webhook. The agent answers with a short comment for developers — right away, or later through
/// the callback URL. The agent itself lives in its own repository; see docs/ai-analysis.md for the contract.
/// </summary>
public sealed class AnalysisOptions
{
    /// <summary>Off by default: test data leaves the bridge, so an admin must switch it on knowingly.</summary>
    public bool Enabled { get; set; }

    /// <summary>The agent's webhook, e.g. https://n8n.example.com/webhook/cbbridge-analysis.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>Optional auth header the bridge sends to the agent (e.g. "Authorization" or n8n Header Auth name).</summary>
    public string AuthHeader { get; set; } = "Authorization";
    /// <summary>Secret value for AuthHeader, e.g. "Bearer …". Set via environment variable Analysis__AuthValue.</summary>
    public string AuthValue { get; set; } = "";

    /// <summary>
    /// Public address of this bridge as the agent can reach it, e.g. https://bridge.example.com. Needed for
    /// asynchronous answers (callback). Empty = the agent must answer in the webhook response itself.
    /// </summary>
    public string CallbackBaseUrl { get; set; } = "";

    /// <summary>Verdicts that trigger an analysis.</summary>
    public string[] Verdicts { get; set; } = Array.Empty<string>();
    public string[] EffectiveVerdicts => Verdicts.Length > 0 ? Verdicts : new[] { "FAILED", "TIMEOUT", "ERROR" };

    /// <summary>How long the webhook call itself may take (synchronous answer included).</summary>
    public int RequestTimeoutSec { get; set; } = 90;
    /// <summary>How long a callback is waited for after the agent accepted the job.</summary>
    public int CallbackWaitSec { get; set; } = 900;

    /// <summary>Fields of the agent's answer that hold the comment (first one found wins).</summary>
    public string CommentFields { get; set; } = "comment,summary,output,text";

    /// <summary>Hints passed to the agent; it decides what to do with them.</summary>
    public string Language { get; set; } = "en";
    public int MaxWords { get; set; } = 80;

    /// <summary>Framework answers longer than this are cut before sending.</summary>
    public int MaxFrameworkChars { get; set; } = 20000;

    /// <summary>Add the comment to the codebeamer test run (only when results are posted back there).</summary>
    public bool PostComment { get; set; } = true;
    /// <summary>ALIGN WITH YOUR INSTANCE: Swagger v3 path for adding a comment to an item.</summary>
    public string CommentPath { get; set; } = "/items/{itemId}/comments";
    public string CommentPrefix { get; set; } = "AI analysis (automatic, please verify):";
}

/// <summary>
/// appsettings.json → "Access": who may write to codebeamer. Everyone with a codebeamer login may run tests;
/// posting results back (and creating test runs, adding comments) can be limited to named accounts.
/// </summary>
public sealed class AccessOptions
{
    /// <summary>codebeamer user names allowed to post back; empty = everyone. Include the CI agent's account.</summary>
    public string[] PostBackUsers { get; set; } = Array.Empty<string>();

    public bool MayPostBack(string user) =>
        PostBackUsers.Length == 0 || PostBackUsers.Contains(user, StringComparer.OrdinalIgnoreCase);
}
