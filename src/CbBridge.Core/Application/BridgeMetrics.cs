using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace CbBridge.Core.Application;

/// <summary>
/// Counters since the bridge started, exposed as Prometheus text at /metrics (Grafana, Zabbix, Datadog …).
/// </summary>
public sealed class BridgeMetrics
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<(string Name, string Labels), long> _counters = new();
    private long _runsActive;

    public void Count(string name, params (string Key, string Value)[] labels) =>
        _counters.AddOrUpdate((name, Labels(labels)), 1, (_, v) => v + 1);

    public void RunStarted() { Interlocked.Increment(ref _runsActive); Count("cbbridge_runs_started_total"); }
    public void RunFinished() { Interlocked.Decrement(ref _runsActive); Count("cbbridge_runs_finished_total"); }

    private static string Labels((string Key, string Value)[] labels) =>
        labels.Length == 0 ? "" : "{" + string.Join(",", labels.Select(l => $"{l.Key}=\"{l.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"")) + "}";

    private static readonly Dictionary<string, string> Help = new()
    {
        ["cbbridge_runs_started_total"] = "Runs started (console, CI, MCP)",
        ["cbbridge_runs_finished_total"] = "Runs finished",
        ["cbbridge_tests_total"] = "Executed tests by verdict",
        ["cbbridge_test_seconds_total"] = "Sum of test durations in seconds",
        ["cbbridge_codebeamer_writes_total"] = "Writes to codebeamer by kind and outcome",
        ["cbbridge_ai_analyses_total"] = "AI analyses by outcome",
        ["cbbridge_events_total"] = "Event deliveries by outcome",
    };

    public string Prometheus(int activeBenches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HELP cbbridge_up_seconds Seconds since the bridge started").AppendLine("# TYPE cbbridge_up_seconds gauge");
        sb.AppendLine("cbbridge_up_seconds " + ((long)(DateTimeOffset.UtcNow - _started).TotalSeconds).ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("# HELP cbbridge_runs_active Runs in progress").AppendLine("# TYPE cbbridge_runs_active gauge");
        sb.AppendLine("cbbridge_runs_active " + Interlocked.Read(ref _runsActive));
        sb.AppendLine("# HELP cbbridge_benches_busy Test systems busy right now").AppendLine("# TYPE cbbridge_benches_busy gauge");
        sb.AppendLine("cbbridge_benches_busy " + activeBenches);
        foreach (var group in _counters.GroupBy(c => c.Key.Name).OrderBy(g => g.Key))
        {
            sb.AppendLine($"# HELP {group.Key} {Help.GetValueOrDefault(group.Key, group.Key)}").AppendLine($"# TYPE {group.Key} counter");
            foreach (var c in group.OrderBy(c => c.Key.Labels))
                sb.AppendLine($"{group.Key}{c.Key.Labels} {c.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        return sb.ToString();
    }
}
