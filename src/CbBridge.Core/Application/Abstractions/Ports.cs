using System.Text.Json.Nodes;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Abstractions;

/// <summary>Reading side — implemented with codebeamer legacy REST.</summary>
public interface ICodebeamerReader
{
    string Describe(string path);
    Task PingAsync(CancellationToken ct = default);
    Task<JsonNode?> GetProjectsAsync(CancellationToken ct = default);
    Task<JsonNode?> GetProjectAsync(string projectId, CancellationToken ct = default);
    Task<JsonNode?> GetCategoriesAsync(string projectId, CancellationToken ct = default);
    Task<JsonNode?> GetOutlineAsync(string categoryId, CancellationToken ct = default);
    Task<JsonNode?> GetItemAsync(string itemId, CancellationToken ct = default);
    Task<JsonNode?> GetReferencesAsync(string itemId, CancellationToken ct = default);
}

/// <summary>Writing side — implemented with codebeamer Swagger REST v3.</summary>
public interface ICodebeamerResultWriter
{
    Task<(PostState State, string Message)> PostResultAsync(ExecutionResult result, CancellationToken ct = default);

    /// <summary>
    /// Run multiple: creates a new test run for one test case. Returns the new run's ID (in dry run a placeholder),
    /// or null with the reason. Never throws.
    /// </summary>
    Task<(string? NewRunId, PostState State, string Message)> CreateTestRunAsync(TestRunPayload testCase, CancellationToken ct = default);

    /// <summary>Contract 1.1: uploads a file the framework returned (log, trace, screenshot) to the test run. Never throws.</summary>
    Task<(PostState State, string Message)> UploadAttachmentAsync(string itemId, string fileName, string contentType, byte[] content, CancellationToken ct = default);

    /// <summary>AI analysis: adds a comment to a codebeamer item (the test run). Never throws.</summary>
    Task<(PostState State, string Message)> PostCommentAsync(string itemId, string text, CancellationToken ct = default);
}

/// <summary>
/// The AI analysis agent, on whatever platform (n8n, Zapier, a Python service …). One call per failed test.
/// </summary>
public interface IAnalysisAgent
{
    /// <summary>
    /// Sends the request. Returns the agent's answer when it replied right away, Accepted when it will call back,
    /// or an error text. Never throws.
    /// </summary>
    Task<AgentReply> RequestAsync(JsonObject request, CancellationToken ct = default);
}

public sealed record AgentReply(JsonNode? Answer, bool Accepted, string? Error);

/// <summary>The Python framework (Flask).</summary>
public interface IFrameworkClient
{
    /// <summary>Framework address used by this user right now (chosen in the UI, default from config).</summary>
    string BaseUrl { get; }

    /// <summary>Checks the framework answers at BaseUrl. Never throws.</summary>
    Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default);

    /// <summary>Never throws for timeouts/aborts — returns a result with the matching verdict.</summary>
    Task<ExecutionResult> ExecuteAsync(TestRunPayload payload, TimeSpan httpWait, CancellationToken abort);
    Task<bool> CancelAsync(string itemId);

    /// <summary>Contract 1.1: live progress of a running test, or null (not running / not supported). Never throws.</summary>
    Task<FrameworkProgress?> ProgressAsync(string itemId, CancellationToken ct = default);
}

/// <summary>What GET /progress/{itemId} reports.</summary>
public sealed record FrameworkProgress(int StepsDone, int? StepsTotal, string? LastStep, bool Supported = true);

/// <summary>Run history (implemented with SQLite). Synchronous and fast; callers catch and log errors.</summary>
public interface IResultRepository
{
    /// <summary>False when the database is switched off or the SQLite library could not be loaded.</summary>
    bool Available { get; }
    /// <summary>"SQLite 3.45.1 · data/cbbridge.db", or why history is not available.</summary>
    string Status { get; }

    void SaveRun(Results.RunRecord run, IReadOnlyList<ExecutionResult> board, RunOptions? options);
    void SaveResult(Results.RunRecord run, int position, TestRunPayload sent, ExecutionResult result);
    void UpdateUpload(string runId, int position, ExecutionResult result);
    void UpdateAnalysis(string runId, int position, ExecutionResult result);
    bool SetFeedback(string runId, int position, int? value, string by, string? note);

    (IReadOnlyList<History.RunSummary> Runs, int Total) ListRuns(History.RunQuery query);
    History.RunSummary? GetRun(string runId);
    IReadOnlyList<History.StoredResult> ResultsOf(string runId);
    History.StoredResult? GetResult(string runId, int position);
    /// <summary>Earlier executions of a test, newest first. Key: "tc:…"/"item:…", or a bare ID (matches both).</summary>
    IReadOnlyList<History.HistoryPoint> History(string testKey, int limit = 50);
    /// <summary>The same test's result in the run before this one.</summary>
    History.StoredResult? Previous(History.StoredResult result);
    /// <summary>The same test's last PASSED result before this one.</summary>
    History.StoredResult? LastPassingBefore(History.StoredResult result);

    void Audit(History.AuditEntry entry);
    IReadOnlyList<History.AuditEntry> AuditLog(int limit = 200, string? target = null);

    /// <summary>Deletes runs older than <paramref name="keepDays"/>. Returns how many.</summary>
    int CleanUp(int keepDays);
}

/// <summary>Tells subscribed systems about runs. Never blocks and never throws.</summary>
public interface IEventPublisher
{
    void Publish(Events.BridgeEvent e);
}

public sealed class CodebeamerException(string message) : Exception(message);
