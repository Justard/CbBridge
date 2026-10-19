using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Api;
using CbBridge.Core.Application.Ci;
using CbBridge.Core.Application.History;
using CbBridge.Web.Api;

namespace CbBridge.Web.Mcp;

/// <summary>Everything a tool call may use: the calling API client, services, and the base URL for links.</summary>
public sealed record McpContext(ApiClient Client, IServiceProvider Services, string BaseUrl, CancellationToken Ct);

/// <summary>A tool error the agent should see (isError result), as opposed to a protocol error.</summary>
public sealed class ToolError(string message) : Exception(message);

public sealed record McpTool(
    string Name, string Title, string Description, string Scope, JsonObject InputSchema, bool ReadOnly,
    Func<JsonObject, McpContext, Task<JsonNode>> Run);

/// <summary>
/// The bridge's MCP tools. Same data as /api/v1 (ApiViews), same scopes. Descriptions are written for the model:
/// what the tool is for, and what to call next.
/// </summary>
public static class McpTools
{
    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description, _) in props)
            properties[name] = type.EndsWith("[]")
                ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = type[..^2] }, ["description"] = description }
                : new JsonObject { ["type"] = type, ["description"] = description };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        var required = props.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return schema;
    }

    private static string? Str(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    private static string Req(JsonObject a, string k) => Str(a, k) ?? throw new ToolError($"'{k}' is required.");
    private static int? Int(JsonObject a, string k) => a[k] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null) : null;
    private static bool? Bool(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static IResultRepository Repo(McpContext c)
    {
        var repo = c.Services.GetRequiredService<IResultRepository>();
        return repo.Available ? repo : throw new ToolError("Run history is not available on this bridge: " + repo.Status);
    }

    public static readonly IReadOnlyList<McpTool> All = new McpTool[]
    {
        new("list_runs", "List test runs",
            "Lists test runs (console, CI and agent runs), newest first, with pass/fail counts. Filter by free text " +
            "(test name, item ID, test configuration such as a firmware version, user, label) or by a verdict the run " +
            "contains. Use get_run next to see the tests of one run.",
            Scopes.HistoryRead,
            Schema(("text", "string", "Free-text filter", false), ("source", "string", "console, ci or mcp", false),
                   ("verdict", "string", "Only runs containing this verdict: FAILED, TIMEOUT, ERROR, BLOCKED, PASSED", false),
                   ("limit", "integer", "Max runs (default 20, max 100)", false)),
            true, (a, c) =>
            {
                var (runs, total) = Repo(c).ListRuns(new RunQuery(Str(a, "text"), Str(a, "source"), Str(a, "verdict"), Limit: Math.Clamp(Int(a, "limit") ?? 20, 1, 100)));
                return Task.FromResult<JsonNode>(new JsonObject
                {
                    ["total"] = total,
                    ["runs"] = new JsonArray(runs.Select(r => (JsonNode?)ApiViews.Run(r, c.BaseUrl)).ToArray())
                });
            }),

        new("get_run", "Get one run",
            "One run with every test: verdict, message, test configuration, AI comment, and the verdict of the same test " +
            "in the previous run (trend 'NEW FAIL' = passed before, fails now). Use get_result or compare_with_last_pass for one test.",
            Scopes.HistoryRead, Schema(("run_id", "string", "Run ID from list_runs", true)),
            true, (a, c) =>
            {
                var repo = Repo(c);
                var id = Req(a, "run_id");
                var run = repo.GetRun(id) ?? throw new ToolError($"No run '{id}'. Use list_runs to find run IDs.");
                var body = ApiViews.Run(run, c.BaseUrl);
                body["results"] = new JsonArray(repo.ResultsOf(id).Select(r => (JsonNode?)ApiViews.Result(r, c.BaseUrl, false, repo.Previous(r))).ToArray());
                return Task.FromResult<JsonNode>(body);
            }),

        new("get_result", "Get one test result",
            "Full detail of one test in one run: the exact request sent to the test framework (steps, parameters, test " +
            "configuration), the framework's full answer (step results, measured values), and the AI analysis.",
            Scopes.HistoryRead,
            Schema(("run_id", "string", "Run ID", true), ("position", "integer", "1-based position of the test in the run", true)),
            true, (a, c) =>
            {
                var repo = Repo(c);
                var r = repo.GetResult(Req(a, "run_id"), Int(a, "position") ?? throw new ToolError("'position' is required."))
                        ?? throw new ToolError("No such result. Use get_run to see positions.");
                return Task.FromResult<JsonNode>(ApiViews.Result(r, c.BaseUrl, true, repo.Previous(r)));
            }),

        new("compare_with_last_pass", "Compare a failure with the last pass",
            "For a failed test: when it last passed, with which test configuration (firmware/release), and which lines of " +
            "the framework's answer changed since then. The best starting point to explain a regression.",
            Scopes.HistoryRead,
            Schema(("run_id", "string", "Run ID", true), ("position", "integer", "1-based position of the test in the run", true)),
            true, (a, c) =>
            {
                var repo = Repo(c);
                var r = repo.GetResult(Req(a, "run_id"), Int(a, "position") ?? throw new ToolError("'position' is required."))
                        ?? throw new ToolError("No such result.");
                var previous = repo.Previous(r);
                var lastPass = r.Verdict == "PASSED" ? null : repo.LastPassingBefore(r);
                return Task.FromResult<JsonNode>(new JsonObject
                {
                    ["result"] = ApiViews.Result(r, c.BaseUrl, false, previous),
                    ["previous"] = previous is null ? null : ApiViews.Result(previous, c.BaseUrl, false),
                    ["lastPassing"] = lastPass is null ? null : ApiViews.Result(lastPass, c.BaseUrl, false),
                    ["responseChangesSinceLastPass"] = ApiViews.Diff(lastPass, r),
                    ["note"] = lastPass is null && r.Verdict != "PASSED" ? "This test has never passed in the recorded history." : null
                });
            }),

        new("test_history", "History of one test",
            "All recorded executions of one test across runs, newest first, with verdict, test configuration and pass rate. " +
            "test_key is 'tc:<test case id>', 'item:<test run id>', or just the ID. Use it to tell a regression from a flaky test.",
            Scopes.HistoryRead,
            Schema(("test_key", "string", "e.g. tc:10232, item:88132 or 10232", true), ("limit", "integer", "Max entries (default 30)", false)),
            true, (a, c) =>
            {
                var key = Req(a, "test_key");
                var points = Repo(c).History(key, Math.Clamp(Int(a, "limit") ?? 30, 1, 500));
                return Task.FromResult<JsonNode>(new JsonObject
                {
                    ["testKey"] = key, ["count"] = points.Count,
                    ["passRate"] = points.Count == 0 ? null : Math.Round(points.Count(p => p.Verdict == "PASSED") / (double)points.Count, 3),
                    ["flips"] = points.Zip(points.Skip(1)).Count(p => (p.First.Verdict == "PASSED") != (p.Second.Verdict == "PASSED")),
                    ["history"] = new JsonArray(points.Select(p => (JsonNode?)ApiViews.Point(p, c.BaseUrl)).ToArray())
                });
            }),

        new("find_new_failures", "Find new failures",
            "Tests that passed in their previous execution and fail now, across the most recent runs — the regressions to " +
            "look at first. Optionally only runs whose selection, label or test configuration matches 'text'.",
            Scopes.HistoryRead,
            Schema(("runs", "integer", "How many recent runs to scan (default 10, max 50)", false), ("text", "string", "Optional filter like list_runs", false)),
            true, (a, c) =>
            {
                var repo = Repo(c);
                var (runs, _) = repo.ListRuns(new RunQuery(Str(a, "text"), Limit: Math.Clamp(Int(a, "runs") ?? 10, 1, 50)));
                var found = new JsonArray();
                foreach (var run in runs)
                    foreach (var r in repo.ResultsOf(run.Id).Where(r => r.IsProblem))
                        if (repo.Previous(r) is { Verdict: "PASSED" } prev)
                        {
                            var item = ApiViews.Result(r, c.BaseUrl, false, prev);
                            item["previousTestConfiguration"] = prev.TestConfiguration;
                            found.Add(item);
                        }
                return Task.FromResult<JsonNode>(new JsonObject { ["runsScanned"] = runs.Count, ["newFailures"] = found });
            }),

        new("rate_ai_comment", "Rate an AI comment",
            "Records whether the AI analysis comment on a failed test was helpful. Used to measure the analysis agent.",
            Scopes.FeedbackWrite,
            Schema(("run_id", "string", "Run ID", true), ("position", "integer", "Position of the test", true),
                   ("helpful", "boolean", "true = helpful, false = not helpful", true), ("note", "string", "Optional reason", false)),
            false, (a, c) =>
            {
                var helpful = Bool(a, "helpful") ?? throw new ToolError("'helpful' (true/false) is required.");
                var ok = Repo(c).SetFeedback(Req(a, "run_id"), Int(a, "position") ?? 0, helpful ? 1 : -1, "mcp:" + c.Client.Name, Str(a, "note"));
                return ok ? Task.FromResult<JsonNode>(new JsonObject { ["saved"] = true }) : throw new ToolError("No such result.");
            }),

        new("audit_log", "Audit log",
            "Who wrote what to codebeamer (results, new test runs, AI comments) and whether it worked. Filter by target " +
            "like 'item:88132' or 'tc:10232'.",
            Scopes.AuditRead,
            Schema(("target", "string", "Optional target filter", false), ("limit", "integer", "Max entries (default 50)", false)),
            true, (a, c) => Task.FromResult<JsonNode>(new JsonObject
            {
                ["entries"] = new JsonArray(Repo(c).AuditLog(Math.Clamp(Int(a, "limit") ?? 50, 1, 1000), Str(a, "target"))
                    .Select(e => (JsonNode?)ApiViews.Audit(e)).ToArray())
            })),

        new("start_run", "Start a test run",
            "Runs tests on a test bench and posts the results to codebeamer, using this client's own codebeamer account " +
            "(configured on the bridge). Give exactly one of category, testablauf, test_case, test_run. Returns a job ID " +
            "immediately — poll get_job until it is finished. This executes real tests on hardware: only start what the user asked for.",
            Scopes.RunsStart,
            Schema(("category", "string", "Category of Testabläufe: all their test cases", false),
                   ("testablauf", "string", "One Testablauf: its test cases", false),
                   ("test_case", "string", "One test case (a new test run is created for it)", false),
                   ("test_run", "string", "One existing test run", false),
                   ("only_test_cases", "string[]", "Optional: only these test case IDs", false),
                   ("release", "string", "Release for the Test Configuration, e.g. R5.0", false),
                   ("versions", "string[]", "Versions, e.g. ['FW 5.0.1']", false),
                   ("framework_url", "string", "Test system address; default: the bridge's", false),
                   ("post_back", "boolean", "Write results to codebeamer (default true)", false),
                   ("analyze", "boolean", "AI analysis of failures (default false)", false),
                   ("time_limit_sec", "integer", "Per test", false)),
            false, async (a, c) =>
            {
                if (c.Client.Codebeamer is not { } creds)
                    throw new ToolError($"API client '{c.Client.Name}' has no codebeamer account configured on the bridge (Api:Clients CodebeamerUser/Password or CodebeamerToken), so it cannot start runs.");
                var request = new CiRunRequest
                {
                    Category = Str(a, "category"), Testablauf = Str(a, "testablauf"), TestCase = Str(a, "test_case"), TestRun = Str(a, "test_run"),
                    OnlyTestCases = a["only_test_cases"] is JsonArray only ? only.Select(x => x?.ToString() ?? "").Where(x => x != "").ToList() : null,
                    Release = Str(a, "release"),
                    Versions = a["versions"] is JsonArray v ? v.Select(x => x?.ToString() ?? "").Where(x => x != "").ToList() : null,
                    FrameworkUrl = Str(a, "framework_url"), PostBack = Bool(a, "post_back") ?? true, Analyze = Bool(a, "analyze") ?? false,
                    TimeLimitSec = Int(a, "time_limit_sec"), Label = $"MCP {c.Client.Name}"
                };
                var (job, status, error) = await c.Services.GetRequiredService<CiRunner>().StartAsync(request, creds, c.Ct, "mcp:" + c.Client.Name);
                if (job is null) throw new ToolError($"Not started ({status}): {error}");
                return new JsonObject { ["jobId"] = job.Id, ["state"] = job.State.ToString(), ["next"] = "Call get_job with this jobId until state is Finished, Failed or Aborted." };
            }),

        new("get_job", "Status of a started run",
            "Progress of a job from start_run (or a CI job): state, per-test verdicts so far, last log lines, and runId for " +
            "get_run once it is finished.",
            Scopes.RunsRead, Schema(("job_id", "string", "Job ID from start_run", true)),
            true, (a, c) =>
            {
                var id = Req(a, "job_id");
                var job = c.Services.GetRequiredService<CiJobStore>().Get(id) ?? throw new ToolError($"No job '{id}' (jobs are kept in memory until the bridge restarts).");
                var view = CiJobView.From(job);
                var node = JsonSerializer.SerializeToNode(view, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
                if (node["log"] is JsonArray log && log.Count > 15)
                    node["log"] = new JsonArray(log.Skip(log.Count - 15).Select(l => l?.DeepClone()).ToArray());
                node["done"] = job.IsDone;
                return Task.FromResult<JsonNode>(node);
            }),
    };
}
