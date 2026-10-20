using System.Text.Json.Nodes;
using CbBridge.Core.Application.Api;
using CbBridge.Core.Application.Results;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Events;

/// <summary>Builds the bridge's events (same shape for every subscriber, see docs/api.md#events).</summary>
public sealed class RunEvents(Abstractions.IEventPublisher publisher, IOptions<BridgeOptions> bridge)
{
    private string? Link(string path) =>
        string.IsNullOrWhiteSpace(bridge.Value.PublicUrl) ? null : bridge.Value.PublicUrl.TrimEnd('/') + path;

    private JsonObject RunNode(RunRecord run) => new()
    {
        ["id"] = run.Id, ["source"] = run.Source, ["startedBy"] = run.StartedBy, ["selection"] = run.Selection,
        ["label"] = run.Label, ["frameworkUrl"] = run.FrameworkUrl, ["link"] = Link($"/history/{run.Id}")
    };

    private JsonObject TestNode(RunRecord run, int position, ExecutionResult r, string? testConfiguration) => new()
    {
        ["position"] = position, ["itemId"] = r.ItemId, ["testCaseId"] = r.TestCaseId, ["testablauf"] = r.GroupName,
        ["name"] = r.Name, ["verdict"] = r.Verdict.ToString().ToUpperInvariant(), ["message"] = r.Message,
        ["durationSec"] = Math.Round(r.Duration.TotalSeconds, 2), ["testConfiguration"] = testConfiguration,
        ["codebeamerTestRunId"] = r.PostTargetId, ["link"] = Link($"/history/{run.Id}/{position}")
    };

    private static JsonObject Envelope(string name, JsonObject body)
    {
        body["event"] = name;
        body["at"] = DateTimeOffset.UtcNow.ToString("O");
        return body;
    }

    public void RunStarted(RunRecord run, int count) => publisher.Publish(new BridgeEvent(EventNames.RunStarted,
        Envelope(EventNames.RunStarted, new JsonObject { ["run"] = RunNode(run), ["tests"] = count }),
        $"▶ Run started: {run.Selection} ({count} tests) by {run.StartedBy} on {run.FrameworkUrl}", false));

    public void RunFinished(RunRecord run, IReadOnlyList<ExecutionResult> board)
    {
        int C(params Verdict[] v) => board.Count(r => v.Contains(r.Verdict));
        var problems = C(Verdict.Failed, Verdict.Timeout, Verdict.Error, Verdict.Blocked);
        var summary = new JsonObject
        {
            ["total"] = board.Count, ["passed"] = C(Verdict.Passed), ["failed"] = C(Verdict.Failed),
            ["timeout"] = C(Verdict.Timeout), ["error"] = C(Verdict.Error), ["blocked"] = C(Verdict.Blocked),
            ["notRun"] = C(Verdict.NotRun, Verdict.Aborted)
        };
        var failed = new JsonArray(board.Select((r, i) => (r, i)).Where(x => x.r.Verdict is Verdict.Failed or Verdict.Timeout or Verdict.Error or Verdict.Blocked)
            .Take(50).Select(x => (JsonNode?)new JsonObject { ["position"] = x.i + 1, ["name"] = x.r.Name, ["verdict"] = x.r.Verdict.ToString().ToUpperInvariant() }).ToArray());
        var link = Link($"/history/{run.Id}");
        publisher.Publish(new BridgeEvent(EventNames.RunFinished,
            Envelope(EventNames.RunFinished, new JsonObject { ["run"] = RunNode(run), ["summary"] = summary, ["problems"] = failed }),
            $"{(problems == 0 ? "✅" : "❌")} {run.Selection}: {C(Verdict.Passed)}/{board.Count} passed" +
            (problems > 0 ? $", {problems} failed" : "") + $" ({run.Source}, {run.StartedBy})" + (link is null ? "" : $" — {link}"),
            problems > 0));
    }

    public void TestFailed(RunRecord run, int position, ExecutionResult r, string? testConfiguration)
    {
        var link = Link($"/history/{run.Id}/{position}");
        publisher.Publish(new BridgeEvent(EventNames.TestFailed,
            Envelope(EventNames.TestFailed, new JsonObject { ["run"] = RunNode(run), ["test"] = TestNode(run, position, r, testConfiguration) }),
            $"❌ {r.Name} {r.Verdict.ToString().ToUpperInvariant()}" + (string.IsNullOrWhiteSpace(r.Message) ? "" : $": {r.Message}") +
            (testConfiguration is null ? "" : $" [{testConfiguration}]") + (link is null ? "" : $" — {link}"), true));
    }

    public void AnalysisDone(RunRecord run, int position, ExecutionResult r)
    {
        var link = Link($"/history/{run.Id}/{position}");
        publisher.Publish(new BridgeEvent(EventNames.AnalysisDone,
            Envelope(EventNames.AnalysisDone, new JsonObject
            {
                ["run"] = RunNode(run), ["test"] = TestNode(run, position, r, null),
                ["analysis"] = new JsonObject { ["state"] = r.AnalysisState.ToString(), ["comment"] = r.AnalysisComment }
            }),
            $"✎ AI on {r.Name}: {r.AnalysisComment}" + (link is null ? "" : $" — {link}"), true));
    }
}
