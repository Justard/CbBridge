using System.Diagnostics;
using CbBridge.Web.Application.Abstractions;
using CbBridge.Web.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Application.Execution;

/// <summary>
/// Runs test runs one by one against the framework, enforces both time limits,
/// and posts results live when "continuous repost" is on.
/// </summary>
public sealed class RunOrchestrator(
    IFrameworkClient framework,
    ICodebeamerResultWriter writer,
    IOptions<FrameworkOptions> frameworkOptions)
{
    public string FrameworkUrl => framework.BaseUrl;

    /// <param name="queue">Edited payloads, in run order.</param>
    /// <param name="board">One entry per queue item (same order); updated in place for the UI.</param>
    public async Task RunAsync(
        IReadOnlyList<TestRunPayload> queue,
        IList<ExecutionResult> board,
        RunOptions options,
        Func<string, Task> log,
        Func<Task> changed,
        CancellationToken abort)
    {
        var fw = frameworkOptions.Value;
        var sw = Stopwatch.StartNew();
        var overall = TimeSpan.FromSeconds(Math.Max(1, options.OverallLimitSec));
        var grace = TimeSpan.FromSeconds(Math.Max(0, fw.GraceSeconds));
        string T() => $"[{sw.Elapsed:mm\\:ss}]";

        var postMode = !options.PostBack ? "off" : options.Continuous ? "continuous" : "after review";
        await log($"{T()} run started · {queue.Count} test run(s) · framework {framework.BaseUrl} · whole-run limit {overall.TotalSeconds:0}s · post-back {postMode}");

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
            await changed();
            await log($"{T()} → POST {fw.ExecutePath}  #{sent.ItemId} \"{sent.Name}\"  timeLimitSec={sent.TimeLimitSec} (bridge waits max {(limit + grace).TotalSeconds:0}s)");

            var result = await framework.ExecuteAsync(sent, limit + grace, abort);
            entry.CopyOutcomeFrom(result);
            var note = string.IsNullOrWhiteSpace(result.Message) ? "" : " · " + result.Message;
            await log($"{T()} ← #{sent.ItemId} {result.Verdict.ToString().ToUpperInvariant()} in {result.Duration.TotalSeconds:0.0}s{note}");
            await changed();

            if (options.PostBack && options.Continuous && entry.CanPost)
            {
                await PostAsync(entry, log, CancellationToken.None, T());
                await changed();
            }
        }

        await log($"{T()} run finished" + (options.PostBack && !options.Continuous ? " · review results, then press Upload" : ""));
    }

    public async Task PostAsync(ExecutionResult result, Func<string, Task> log, CancellationToken ct, string stamp = "")
    {
        if (!result.CanPost)
        {
            var why = result.IsLocal ? "local test, not in codebeamer" : $"{result.Verdict} is not a codebeamer result";
            await log($"{stamp} skip upload #{result.ItemId}: {why}".Trim());
            return;
        }
        var (state, message) = await writer.PostResultAsync(result, ct);
        result.Posted = state;
        result.PostMessage = message;
        await log($"{stamp} ↑ #{result.ItemId} {message}".Trim());
    }
}
