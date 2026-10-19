using System.Text.Json.Nodes;

namespace CbBridge.Core.Application.Events;

/// <summary>appsettings.json → "Events": systems that want to hear about runs (Teams, Slack, n8n, dashboards …).</summary>
public sealed class EventOptions
{
    public List<EventSubscriber> Subscribers { get; set; } = new();
}

public sealed class EventSubscriber
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Which events: run.started, run.finished, test.failed, analysis.done. Empty = all.</summary>
    public string[] Events { get; set; } = Array.Empty<string>();
    /// <summary>"json" (full event, signed) or "text" ({"text": "…"} — works for Teams and Slack incoming webhooks).</summary>
    public string Format { get; set; } = "json";
    /// <summary>HMAC-SHA256 secret: the body's signature is sent as X-Bridge-Signature: sha256=…</summary>
    public string Secret { get; set; } = "";
    /// <summary>run.finished only when something failed (for notification channels).</summary>
    public bool OnlyProblems { get; set; }
    public bool Disabled { get; set; }
}

public static class EventNames
{
    public const string RunStarted = "run.started";
    public const string RunFinished = "run.finished";
    public const string TestFailed = "test.failed";
    public const string AnalysisDone = "analysis.done";
    public static readonly string[] All = { RunStarted, RunFinished, TestFailed, AnalysisDone };
}

/// <summary>One event: machine-readable data plus a one-line text for chat channels.</summary>
public sealed record BridgeEvent(string Name, JsonObject Data, string Text, bool IsProblem);
