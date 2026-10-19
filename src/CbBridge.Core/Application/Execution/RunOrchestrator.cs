using System.Diagnostics;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Execution;

/// <summary>
/// Runs test runs one by one against the framework, enforces both time limits,
/// and posts results live when "continuous repost" is on.
/// </summary>
public sealed class RunOrchestrator(
    IFrameworkClient framework,
    ICodebeamerResultWriter writer,
    IOptions<FrameworkOptions> frameworkOptions,
    IOptions<AnalysisOptions> analysisOptions,
    Analysis.FailureAnalyzer analyzer,
    Infrastructure.Codebeamer.CodebeamerSession session,
    Results.RunRecorder store,
    Events.RunEvents events,
    BenchRegistry benches,
    IOptions<AccessOptions> access,
    BridgeMetrics metrics,
    Licensing.LicenceService licence) : IDisposable
{
    public string FrameworkUrl => framework.BaseUrl;

    /// <summary>AI analysis is configured (Analysis:Enabled + WebhookUrl) — the UI shows its switch only then.</summary>
    public bool AnalysisAvailable => analyzer.Enabled;

    // AI analyses run in the background: the next test does not wait for the agent.
    private readonly List<Task> _analyses = new();
    private readonly CancellationTokenSource _stop = new();   // tab closed / CI scope ended → stop waiting
    private RunOptions _runOptions = new();

    public int PendingAnalyses { get { lock (_analyses) return _analyses.Count(t => !t.IsCompleted); } }

    /// <param name="queue">Edited payloads, in run order.</param>
    /// <param name="board">One entry per queue item (same order); updated in place for the UI.</param>
    public async Task RunAsync(
        IReadOnlyList<TestRunPayload> queue,
        IList<ExecutionResult> board,
        RunOptions options,
        Func<string, Task> log,
        Func<Task> changed,
        CancellationToken abort,
        Results.RunRecord? record = null)
    {
        if (!licence.AllowsUse)   // second line of defence — UI, CI, API and MCP already refuse
        {
            foreach (var r in board) { r.Verdict = Verdict.NotRun; r.Message = "no valid licence"; }
            await log("not started: " + licence.Check.Message);
            await changed();
            return;
        }
        var bench = framework.BaseUrl;
        benches.Begin(bench, session.User, record?.Source ?? "console", record?.Selection ?? $"{queue.Count} test(s)", queue.Count, record?.Id);
        metrics.RunStarted();
        try { await RunCoreAsync(queue, board, options, log, changed, abort, record); }
        finally
        {
            benches.End(bench);
            metrics.RunFinished();
            foreach (var r in board.Where(r => r.Verdict is not (Verdict.Queued or Verdict.Running)))
            {
                metrics.Count("cbbridge_tests_total", ("verdict", r.Verdict.ToString().ToUpperInvariant()), ("source", record?.Source ?? "console"));
            }
        }
    }

    private async Task RunCoreAsync(
        IReadOnlyList<TestRunPayload> queue,
        IList<ExecutionResult> board,
        RunOptions options,
        Func<string, Task> log,
        Func<Task> changed,
        CancellationToken abort,
        Results.RunRecord? record)
    {
        var fw = frameworkOptions.Value;
        if ((options.PostBack || options.CreateNewRuns) && !access.Value.MayPostBack(session.User))
        {
            await log($"{session.User} may not write to codebeamer (Access:PostBackUsers) → post-back and new test runs are off for this run");
            options.PostBack = false;
            options.CreateNewRuns = false;
        }
        _runOptions = options;
        if (record is not null && store.Begin(record) is { } folder)
            await log($"results are saved to {folder}");
        if (record is not null) events.RunStarted(record, queue.Count);
        var sw = Stopwatch.StartNew();
        var overall = TimeSpan.FromSeconds(Math.Max(1, options.OverallLimitSec));
        var grace = TimeSpan.FromSeconds(Math.Max(0, fw.GraceSeconds));
        string T() => $"[{sw.Elapsed:mm\\:ss}]";

        var postMode = !options.PostBack ? "off" : options.Continuous ? "continuous" : "after review";
        await log($"{T()} run started · {queue.Count} test run(s) · framework {framework.BaseUrl} · whole-run limit {overall.TotalSeconds:0}s · post-back {postMode}" +
                  (options.Analyze && analyzer.Enabled ? " · AI analysis of failures on" : ""));

        for (var i = 0; i < queue.Count; i++)
        {
            var remaining = overall - sw.Elapsed;
            if (abort.IsCancellationRequested || remaining <= TimeSpan.Zero)
            {
                var why = abort.IsCancellationRequested ? "aborted by user" : "whole-run limit reached";
                for (var k = i; k < queue.Count; k++)
                {
                    board[k].Verdict = Verdict.NotRun;
                    board[k].Message = why;
                }
                await log($"{T()} {why} → {queue.Count - i} test run(s) not started");
                await changed();
                break;
            }

            var payload = queue[i];
            var entry = board[i];
            var limit = TimeSpan.FromSeconds(payload.TimeLimitSec > 0 ? payload.TimeLimitSec : options.DefaultTimeLimitSec);
            if (limit > remaining)
            {
                limit = remaining;
                await log($"{T()} #{payload.ItemId}: limit cut to {limit.TotalSeconds:0}s by the whole-run limit");
            }

            var sent = payload.WithTimeLimit((int)Math.Ceiling(limit.TotalSeconds));
            entry.Verdict = Verdict.Running;
            benches.Progress(framework.BaseUrl, i, payload.Name);
            await changed();

            // Run multiple: a test case gets its own new codebeamer test run just before it executes,
            // so nothing is created for test cases that never start (abort, whole-run limit).
            string? creationProblem = null;
            if (sent.SourceTestCaseId is not null && options.CreateNewRuns)
            {
                var (newId, state, message) = await writer.CreateTestRunAsync(sent, CancellationToken.None);
                store.Audit(session.User, "create test run", "tc:" + sent.SourceTestCaseId, state, message);
                await log($"{T()} + test case #{sent.SourceTestCaseId}: {message}");
                if (newId is not null)
                {
                    entry.PostTargetId = newId;
                    if (state == PostState.Posted)
                    {
                        sent.ItemId = newId;   // the framework (and /cancel) now see the new test run
                        entry.ItemId = newId;
                    }
                }
                else
                {
                    creationProblem = "no test run created, result stays local";
                }
                await changed();
            }

            await log($"{T()} → POST {fw.ExecutePath}  #{sent.ItemId} \"{sent.Name}\"  timeLimitSec={sent.TimeLimitSec} (bridge waits max {(limit + grace).TotalSeconds:0}s)");

            // contract 1.1: poll live step progress while the test runs (stops on its own if not supported)
            using var progressStop = new CancellationTokenSource();
            var poll = PollProgressAsync(sent.ItemId, entry, changed, progressStop.Token);
            var result = await framework.ExecuteAsync(sent, limit + grace, abort);
            progressStop.Cancel();
            await poll;
            entry.CopyOutcomeFrom(result);
            if (creationProblem is not null)
                entry.Message = string.IsNullOrWhiteSpace(entry.Message) ? creationProblem : $"{entry.Message} · {creationProblem}";
            var note = string.IsNullOrWhiteSpace(result.Message) ? "" : " · " + result.Message;
            await log($"{T()} ← #{sent.ItemId} {result.Verdict.ToString().ToUpperInvariant()} in {result.Duration.TotalSeconds:0.0}s{note}");
            await changed();

            if (options.PostBack && options.Continuous && entry.CanPost)
            {
                await PostAsync(entry, log, CancellationToken.None, T());
                await changed();
            }

            if (record is not null) await store.SaveResultAsync(record, i + 1, sent, entry);
            if (record is not null && entry.Verdict is Verdict.Failed or Verdict.Timeout or Verdict.Error or Verdict.Blocked)
                events.TestFailed(record, i + 1, entry, sent.TestConfiguration);

            if (analyzer.Wants(options, entry))
                await StartAnalysisAsync(sent, entry, i + 1, record, log, changed, T());
        }

        if (record is not null)
        {
            record.FinishedAt = DateTimeOffset.Now;
            await store.SaveRunAsync(record, board, options);
            events.RunFinished(record, board.ToList());
        }
        var pending = PendingAnalyses;
        await log($"{T()} run finished" + (options.PostBack && !options.Continuous ? " · review results, then press Upload" : "") +
                  (pending > 0 ? $" · {pending} AI analysis/analyses still running, comments appear when they arrive" : ""));
    }

    private async Task PollProgressAsync(string itemId, ExecutionResult entry, Func<Task> changed, CancellationToken stop)
    {
        var every = frameworkOptions.Value.ProgressPollSeconds;
        if (every <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(every), stop);
            while (!stop.IsCancellationRequested)
            {
                var p = await framework.ProgressAsync(itemId, stop);
                if (p is { Supported: false }) return;
                if (p is not null && (p.StepsDone != entry.StepsDone || p.LastStep != entry.LastStep))
                {
                    entry.StepsDone = p.StepsDone;
                    entry.StepsTotal = p.StepsTotal;
                    entry.LastStep = p.LastStep;
                    await Quietly(changed);
                }
                await Task.Delay(TimeSpan.FromSeconds(every), stop);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Contract 1.1: files in the framework's answer (attachments: [{name, contentType, contentBase64}]) go to the
    /// codebeamer test run together with the result.
    /// </summary>
    private async Task UploadAttachmentsAsync(ExecutionResult result, Func<string, Task> log, string stamp)
    {
        if (result.PostTargetId is null || string.IsNullOrWhiteSpace(result.RawJson)) return;
        System.Text.Json.Nodes.JsonArray? files;
        try { files = System.Text.Json.Nodes.JsonNode.Parse(result.RawJson)?[frameworkOptions.Value.AttachmentsField] as System.Text.Json.Nodes.JsonArray; }
        catch (System.Text.Json.JsonException) { return; }
        if (files is null || files.Count == 0) return;

        var max = frameworkOptions.Value.MaxAttachmentMb * 1024L * 1024L;
        foreach (var f in files.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var name = Path.GetFileName(f["name"]?.ToString() ?? "attachment.bin");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(f["contentBase64"]?.ToString() ?? ""); }
            catch (FormatException) { await log($"{stamp} skip attachment {name}: contentBase64 is not valid base64".Trim()); continue; }
            if (bytes.Length == 0 || bytes.Length > max)
            {
                await log($"{stamp} skip attachment {name}: {bytes.Length} bytes (limit {frameworkOptions.Value.MaxAttachmentMb} MB)".Trim());
                continue;
            }
            var (state, message) = await writer.UploadAttachmentAsync(result.PostTargetId, name, f["contentType"]?.ToString() ?? "application/octet-stream", bytes);
            store.Audit(session.User, "attach " + name, "item:" + result.PostTargetId, state, message);
            await log($"{stamp} 📎 #{result.ItemId} {message}".Trim());
        }
    }

    /// <summary>
    /// Sends one failed test to the AI agent without blocking the run. When the answer arrives: shown in the
    /// results, saved in the result file, and added to the codebeamer test run if its result was posted there.
    /// </summary>
    private async Task StartAnalysisAsync(TestRunPayload sent, ExecutionResult entry, int position, Results.RunRecord? record,
        Func<string, Task> log, Func<Task> changed, string stamp)
    {
        entry.AnalysisState = AnalysisState.Pending;
        await log($"{stamp} ? #{entry.ItemId}: AI analysis requested");
        await changed();
        var cbBase = session.BaseUrl;

        var task = Task.Run(async () =>
        {
            var outcome = await analyzer.AnalyzeAsync(sent, entry, record, cbBase, _stop.Token);
            entry.AnalysisState = outcome.State;
            metrics.Count("cbbridge_ai_analyses_total", ("outcome", outcome.State.ToString()));
            entry.AnalysisComment = outcome.Comment;
            entry.AnalysisRawJson = outcome.RawJson;
            await Quietly(() => log(outcome.State == AnalysisState.Done
                ? $"✎ #{entry.ItemId}: AI analysis {outcome.Message}: {outcome.Comment}"
                : $"✎ #{entry.ItemId}: AI analysis {outcome.State}: {outcome.Message}"));

            await TryPostCommentAsync(entry, log);
            if (record is not null) await store.UpdateAnalysisAsync(record, position, entry);
            if (record is not null && outcome.State == AnalysisState.Done) events.AnalysisDone(record, position, entry);
            await Quietly(changed);
        });
        lock (_analyses) _analyses.Add(task);
    }

    /// <summary>
    /// The comment goes to codebeamer once both are true: the analysis is done and the test's result was uploaded
    /// (live, or later with Upload results). Whichever happens second posts it — exactly once.
    /// </summary>
    private async Task TryPostCommentAsync(ExecutionResult entry, Func<string, Task> log)
    {
        if (!analysisOptions.Value.PostComment || !_runOptions.PostBack) return;
        if (entry.AnalysisState != AnalysisState.Done || string.IsNullOrWhiteSpace(entry.AnalysisComment)) return;
        if (entry.Posted is not (PostState.Posted or PostState.DryRun) || entry.PostTargetId is null) return;
        if (!entry.ClaimCommentPost()) return;

        var (state, message) = await writer.PostCommentAsync(entry.PostTargetId, entry.AnalysisComment, CancellationToken.None);
        store.Audit(session.User, "add AI comment", "item:" + entry.PostTargetId, state, message);
        entry.AnalysisPosted = state;
        entry.AnalysisPostMessage = message;
        await Quietly(() => log($"✎ #{entry.ItemId}: comment {message}"));
    }

    /// <summary>CI: let open analyses finish (up to <paramref name="max"/>) so the JUnit report can include them.</summary>
    public async Task WaitForAnalysesAsync(TimeSpan max, CancellationToken ct = default)
    {
        Task[] open;
        lock (_analyses) open = _analyses.Where(t => !t.IsCompleted).ToArray();
        if (open.Length == 0) return;
        try { await Task.WhenAny(Task.WhenAll(open), Task.Delay(max, ct)); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Logging / UI refresh from a background task: the browser tab may be gone by then.</summary>
    private static async Task Quietly(Func<Task> action)
    {
        try { await action(); } catch { /* circuit closed — the result file still gets the analysis */ }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Upload after review (console): posts, then refreshes the saved files.</summary>
    public async Task PostAndRecordAsync(ExecutionResult result, int position, Func<string, Task> log, Results.RunRecord? record)
    {
        await PostAsync(result, log, CancellationToken.None);
        if (record is not null) await store.UpdateUploadAsync(record, position, result);
        await TryPostCommentAsync(result, log);
        if (record is not null && result.AnalysisState != AnalysisState.None) await store.UpdateAnalysisAsync(record, position, result);
    }

    public Task SaveRunRecordAsync(Results.RunRecord? record, IEnumerable<ExecutionResult> board) =>
        record is null ? Task.CompletedTask : store.SaveRunAsync(record, board);

    public async Task PostAsync(ExecutionResult result, Func<string, Task> log, CancellationToken ct, string stamp = "")
    {
        if (!result.CanPost)
        {
            var why = result.IsLocal ? "local test, not in codebeamer" : $"{result.Verdict} is not a codebeamer result";
            await log($"{stamp} skip upload #{result.ItemId}: {why}".Trim());
            return;
        }
        var (state, message) = await writer.PostResultAsync(result, ct);
        store.Audit(session.User, "post result " + result.Verdict.ToString().ToUpperInvariant(), "item:" + result.PostTargetId, state, message);
        result.Posted = state;
        result.PostMessage = message;
        await log($"{stamp} ↑ #{result.ItemId} {message}".Trim());
        if (state is PostState.Posted or PostState.DryRun) await UploadAttachmentsAsync(result, log, stamp);
    }
}
