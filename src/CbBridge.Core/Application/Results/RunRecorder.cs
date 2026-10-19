using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.History;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Results;

/// <summary>
/// Everything that is kept about a run, in one place: the result files (ResultStore) and the run history
/// database (IResultRepository), plus the audit log of codebeamer writes. Never throws — a storage problem is
/// logged and the run goes on.
/// </summary>
public sealed class RunRecorder(ResultStore files, IResultRepository history, BridgeMetrics metrics, ILogger<RunRecorder> logger)
{
    public bool HistoryAvailable => history.Available;

    public string? Begin(RunRecord run)
    {
        var folder = files.Begin(run);
        Db(() => history.SaveRun(run, Array.Empty<ExecutionResult>(), null), "start run " + run.Id);
        return folder;
    }

    public async Task SaveResultAsync(RunRecord run, int position, TestRunPayload sent, ExecutionResult result)
    {
        await files.SaveResultAsync(run, position, sent, result);
        Db(() => history.SaveResult(run, position, sent, result), $"save result {run.Id}/{position}");
    }

    public async Task SaveRunAsync(RunRecord run, IEnumerable<ExecutionResult> board, RunOptions? options = null)
    {
        var list = board.ToList();
        await files.SaveRunAsync(run, list, options);
        Db(() => history.SaveRun(run, list, options), "save run " + run.Id);
    }

    public async Task UpdateUploadAsync(RunRecord run, int position, ExecutionResult result)
    {
        await files.UpdateUploadAsync(run, position, result);
        Db(() => history.UpdateUpload(run.Id, position, result), $"update upload {run.Id}/{position}");
    }

    public async Task UpdateAnalysisAsync(RunRecord run, int position, ExecutionResult result)
    {
        await files.UpdateAnalysisAsync(run, position, result);
        Db(() => history.UpdateAnalysis(run.Id, position, result), $"update analysis {run.Id}/{position}");
    }

    /// <summary>Audit log: every write to codebeamer (and other changes) with who, what, outcome.</summary>
    public void Audit(string actor, string action, string target, PostState outcome, string? detail)
    {
        metrics.Count("cbbridge_codebeamer_writes_total", ("kind", action.Split(' ')[0] + (action.StartsWith("post result") ? "_result" : action.StartsWith("create") ? "_test_run" : action.StartsWith("add AI") ? "_comment" : "")), ("outcome", outcome.ToString()));
        Db(() => history.Audit(new AuditEntry(DateTimeOffset.UtcNow, actor, action, target, outcome.ToString(), detail)),
            $"audit {action} {target}");
    }

    private void Db(Action work, string what)
    {
        if (!history.Available) return;
        try { work(); }
        catch (Exception ex) { logger.LogWarning(ex, "Run history: could not {What}", what); }
    }
}
