using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application.History;

namespace CbBridge.Web.Api;

/// <summary>JSON shapes of /api/v1 (documented in wwwroot/api/openapi.json). Same shapes are used by the MCP tools.</summary>
public static class ApiViews
{
    public static string BaseUrl(HttpContext? http, string? publicUrl) =>
        !string.IsNullOrWhiteSpace(publicUrl) ? publicUrl.TrimEnd('/')
        : http is null ? "" : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

    public static JsonObject Run(RunSummary r, string baseUrl) => new()
    {
        ["id"] = r.Id, ["source"] = r.Source, ["startedBy"] = r.StartedBy, ["label"] = r.Label,
        ["selection"] = r.Selection, ["frameworkUrl"] = r.FrameworkUrl,
        ["startedAt"] = r.StartedAt.ToString("O"), ["finishedAt"] = r.FinishedAt?.ToString("O"),
        ["summary"] = new JsonObject
        {
            ["total"] = r.Total, ["passed"] = r.Passed, ["failed"] = r.Failed, ["blocked"] = r.Blocked,
            ["timeout"] = r.Timeout, ["error"] = r.Error, ["notRun"] = r.NotRun
        },
        ["link"] = $"{baseUrl}/history/{r.Id}"
    };

    /// <summary>Short form (lists); <paramref name="full"/> adds request, framework answer and the agent's answer.</summary>
    public static JsonObject Result(StoredResult r, string baseUrl, bool full, StoredResult? previous = null)
    {
        var o = new JsonObject
        {
            ["runId"] = r.RunId, ["position"] = r.Position, ["testKey"] = r.TestKey, ["itemId"] = r.ItemId,
            ["testCaseId"] = r.TestCaseId, ["testablauf"] = r.Testablauf, ["name"] = r.Name, ["isLocal"] = r.IsLocal,
            ["testConfiguration"] = r.TestConfiguration, ["verdict"] = r.Verdict, ["durationSec"] = r.DurationSec,
            ["message"] = r.Message, ["codebeamerTestRunId"] = r.PostedTo, ["upload"] = r.Upload,
            ["savedAt"] = r.SavedAt.ToString("O"),
            ["analysis"] = r.AnalysisState is null ? null : new JsonObject
            {
                ["state"] = r.AnalysisState, ["comment"] = r.AnalysisComment, ["commentUpload"] = r.CommentUpload,
                ["feedback"] = r.Feedback, ["feedbackBy"] = r.FeedbackBy
            },
            ["link"] = $"{baseUrl}/history/{r.RunId}/{r.Position}"
        };
        if (previous is not null)
        {
            o["previousVerdict"] = previous.Verdict;
            o["trend"] = JsonDiff.Trend(previous.Verdict, r.Verdict);
        }
        if (full)
        {
            o["request"] = Parse(r.RequestJson);
            o["response"] = Parse(r.ResponseJson);
            o["uploadMessage"] = r.UploadMessage;
            if (o["analysis"] is JsonObject a) a["agentAnswer"] = Parse(r.AnalysisJson);
        }
        return o;
    }

    public static JsonObject Point(HistoryPoint p, string baseUrl) => new()
    {
        ["runId"] = p.RunId, ["position"] = p.Position, ["at"] = p.At.ToString("O"), ["verdict"] = p.Verdict,
        ["durationSec"] = p.DurationSec, ["testConfiguration"] = p.TestConfiguration, ["source"] = p.Source,
        ["label"] = p.Label, ["aiComment"] = p.AnalysisComment, ["link"] = $"{baseUrl}/history/{p.RunId}/{p.Position}"
    };

    public static JsonObject Audit(AuditEntry e) => new()
    {
        ["at"] = e.At.ToString("O"), ["actor"] = e.Actor, ["action"] = e.Action, ["target"] = e.Target,
        ["outcome"] = e.Outcome, ["detail"] = e.Detail
    };

    /// <summary>Changed lines between the last passing run and this one (framework answer).</summary>
    public static JsonArray Diff(StoredResult? before, StoredResult after) =>
        before is null ? new JsonArray()
        : new JsonArray(JsonDiff.Lines(before.ResponseJson, after.ResponseJson)
            .Where(l => l.Kind != DiffKind.Same)
            .Select(l => (JsonNode?)JsonValue.Create((l.Kind == DiffKind.Added ? "+ " : "- ") + l.Text.Trim())).ToArray());

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(json); }
    }
}
