using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Execution;
using CbBridge.Core.Application.Links;
using CbBridge.Core.Application.Selection;
using CbBridge.Core.Domain;
using CbBridge.Core.Infrastructure.Codebeamer;
using CbBridge.Core.Infrastructure.Framework;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Ci;

/// <summary>Credentials of the CI agent's codebeamer account, taken from the request. Never stored.</summary>
public sealed record CiCredentials(string User, string Secret, bool IsToken);

/// <summary>In-memory list of CI jobs, plus one lock per test system so two jobs never drive the same bench at once.</summary>
public sealed class CiJobStore(IOptions<CiOptions> options)
{
    private readonly ConcurrentDictionary<string, CiJob> _jobs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _benchLocks = new(StringComparer.OrdinalIgnoreCase);

    public CiJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public IEnumerable<CiJob> All() => _jobs.Values.OrderByDescending(j => j.CreatedAt);

    public void Add(CiJob job)
    {
        _jobs[job.Id] = job;
        // keep the newest N finished jobs
        foreach (var old in _jobs.Values.Where(j => j.IsDone).OrderByDescending(j => j.CreatedAt).Skip(Math.Max(10, options.Value.KeepJobs)))
            _jobs.TryRemove(old.Id, out _);
    }

    public SemaphoreSlim BenchLock(string frameworkUrl) => _benchLocks.GetOrAdd(frameworkUrl, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Starts and executes CI jobs with the same selection and run logic as the browser UI. Each job gets its own
/// DI scope, so it has its own codebeamer session (the CI agent's account) and its own framework target.
/// </summary>
public sealed class CiRunner(
    IServiceScopeFactory scopes,
    CiJobStore store,
    IOptions<CiOptions> ciOptions,
    IOptions<CodebeamerOptions> cbOptions,
    IOptions<FrameworkOptions> fwOptions,
    ILogger<CiRunner> logger)
{
    /// <summary>
    /// Checks the request and the credentials (one codebeamer read call), then starts the job in the background.
    /// Returns the job, or an error text with an HTTP status.
    /// </summary>
    public async Task<(CiJob? Job, int Status, string? Error)> StartAsync(CiRunRequest request, CiCredentials credentials, CancellationToken ct,
        string? client = null)
    {
        var selector = request.Selector();
        if (selector is null)
            return (null, 400, "Give exactly one of: category, testablauf, testRun, testCase (ID or codebeamer link).");

        var id = LinkParser.Parse(selector.Value.Value).Id;
        if (id is null)
            return (null, 400, $"'{selector.Value.Value}' is not an ID or a codebeamer link.");

        var frameworkUrl = FrameworkTarget.Normalize(string.IsNullOrWhiteSpace(request.FrameworkUrl) ? fwOptions.Value.BaseUrl : request.FrameworkUrl);
        if (frameworkUrl is null)
            return (null, 400, $"frameworkUrl '{request.FrameworkUrl}' is not a valid http(s) address.");

        var cbUrl = string.IsNullOrWhiteSpace(request.CodebeamerUrl) ? cbOptions.Value.DefaultBaseUrl : request.CodebeamerUrl!;

        // credentials check before accepting the job, so Jenkins gets a clear 401 instead of a failed job
        await using (var probe = scopes.CreateAsyncScope())
        {
            var session = probe.ServiceProvider.GetRequiredService<CodebeamerSession>();
            session.Configure(cbUrl, credentials.User, credentials.Secret, credentials.IsToken);
            try { await probe.ServiceProvider.GetRequiredService<ICodebeamerReader>().PingAsync(ct); }
            catch (CodebeamerException ex) { return (null, ex.Message.Contains("HTTP 401") || ex.Message.Contains("HTTP 403") ? 401 : 502, ex.Message); }
        }

        var job = new CiJob
        {
            Kind = selector.Value.Kind,
            Target = id,
            FrameworkUrl = frameworkUrl,
            Label = request.Label,
            RequestedBy = credentials.IsToken ? "(token)" : credentials.User,
            Client = client
        };
        store.Add(job);
        job.AddLog($"accepted: {job.Kind} {id} · framework {frameworkUrl} · codebeamer {cbUrl} as {job.RequestedBy}" +
                   (client is null ? "" : $" · client {client}") +
                   (string.IsNullOrWhiteSpace(job.Label) ? "" : $" · {job.Label}"));

        _ = Task.Run(() => ExecuteAsync(job, request, credentials, cbUrl));
        return (job, 202, null);
    }

    private async Task ExecuteAsync(CiJob job, CiRunRequest request, CiCredentials credentials, string cbUrl)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var benchLock = store.BenchLock(job.FrameworkUrl);
        var lockTaken = false;
        try
        {
            var session = sp.GetRequiredService<CodebeamerSession>();
            session.Configure(cbUrl, credentials.User, credentials.Secret, credentials.IsToken);
            session.MarkSignedIn();
            sp.GetRequiredService<FrameworkTarget>().TrySet(job.FrameworkUrl);

            var queue = await BuildQueueAsync(job, request, sp.GetRequiredService<TestSelection>());
            if (queue.Count == 0)
            {
                job.Error = "Nothing to run: no test cases / test runs found for this selection.";
                job.AddLog(job.Error);
                job.State = CiJobState.Failed;
                return;
            }
            job.Results.AddRange(queue.Select(ExecutionResult.ForPayload));

            if (benchLock.CurrentCount == 0)
                job.AddLog($"waiting: test system {job.FrameworkUrl} is busy with another CI job");
            await benchLock.WaitAsync(job.Abort.Token);
            lockTaken = true;

            job.State = CiJobState.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            var options = new RunOptions
            {
                DefaultTimeLimitSec = request.TimeLimitSec ?? ciOptions.Value.DefaultTimeLimitSec,
                OverallLimitSec = request.OverallLimitSec ?? ciOptions.Value.DefaultOverallLimitSec,
                PostBack = request.PostBack,
                Continuous = true,               // CI: post each result as soon as it is known
                CreateNewRuns = request.CreateNewRuns,
                Analyze = request.Analyze
            };
            var record = new Results.RunRecord
            {
                Source = job.Client is { } c && c.StartsWith("mcp:") ? "mcp" : "ci",
                StartedBy = job.RequestedBy,
                Selection = $"{job.Kind} {job.Target}",
                FrameworkUrl = job.FrameworkUrl,
                Label = job.Label
            };
            job.RunId = record.Id;
            var orchestrator = sp.GetRequiredService<RunOrchestrator>();
            if (request.Analyze && !orchestrator.AnalysisAvailable)
                job.AddLog("analyze requested, but AI analysis is not configured on the bridge (Analysis:Enabled / WebhookUrl) — skipped");
            await orchestrator.RunAsync(
                queue, job.Results, options,
                line => { job.AddLog(line); return Task.CompletedTask; },
                () => Task.CompletedTask,
                job.Abort.Token,
                record);

            if (orchestrator.PendingAnalyses > 0)
            {
                var wait = TimeSpan.FromSeconds(Math.Max(0, ciOptions.Value.AnalysisWaitSec));
                job.AddLog($"waiting up to {wait.TotalSeconds:0}s for {orchestrator.PendingAnalyses} AI analysis/analyses");
                await orchestrator.WaitForAnalysesAsync(wait, job.Abort.Token);
                await orchestrator.SaveRunRecordAsync(record, job.Results);
            }

            job.State = job.Abort.IsCancellationRequested ? CiJobState.Aborted : CiJobState.Finished;
            var s = CiSummary.From(job.Results.ToList());
            job.AddLog($"summary: {s.Passed}/{s.Total} passed · failed {s.Failed} · timeout {s.Timeout} · error {s.Error} · not run {s.NotRun}");
        }
        catch (OperationCanceledException) when (job.Abort.IsCancellationRequested)
        {
            job.State = CiJobState.Aborted;
            foreach (var r in job.Results.Where(r => r.Verdict == Verdict.Queued)) { r.Verdict = Verdict.NotRun; r.Message = "aborted"; }
            job.AddLog("aborted before it started");
        }
        catch (Exception ex)
        {
            job.State = CiJobState.Failed;
            job.Error = ex is CodebeamerException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            job.AddLog("failed: " + job.Error);
            logger.LogWarning(ex, "CI job {JobId} failed", job.Id);
        }
        finally
        {
            if (lockTaken) benchLock.Release();
            job.FinishedAt = DateTimeOffset.UtcNow;
            sp.GetRequiredService<CodebeamerSession>().SignOut();
        }
    }

    /// <summary>Same selection as the UI: Testabläufe → test cases, one Testablauf, one test run, or one test case.</summary>
    private async Task<List<TestRunPayload>> BuildQueueAsync(CiJob job, CiRunRequest request, TestSelection selection)
    {
        var limit = request.TimeLimitSec ?? ciOptions.Value.DefaultTimeLimitSec;
        var releaseVersion = TestSelection.ReleaseVersion(request.Release, request.Versions);
        var only = request.OnlyTestCases?.Select(x => LinkParser.Parse(x).Id ?? x).ToHashSet();
        var cache = new Dictionary<string, JsonNode>();
        var queue = new List<TestRunPayload>();

        async Task AddGroupAsync(TestablaufGroup g)
        {
            if (g.Error is not null) job.AddLog($"Testablauf #{g.Id} {g.Name}: {g.Error}");
            foreach (var c in g.Cases.Where(c => only is null || only.Contains(c.Id)))
                queue.Add(await selection.TestCasePayloadAsync(c.Id, g, releaseVersion, limit, cache));
        }

        switch (job.Kind)
        {
            case "category":
                var groups = await selection.LoadTestablaeufeAsync(job.Target, m => { job.AddLog(m); return Task.CompletedTask; });
                job.AddLog($"{groups.Count} Testabläufe · {groups.Sum(g => g.Cases.Count)} test cases");
                foreach (var g in groups) await AddGroupAsync(g);
                break;
            case "testablauf":
                var group = await selection.LoadTestablaufAsync(job.Target);
                job.AddLog($"Testablauf #{group.Id} {group.Name} · {group.Cases.Count} test cases");
                await AddGroupAsync(group);
                break;
            case "testRun":
                queue.Add(await selection.TestRunPayloadAsync(job.Target, limit));
                break;
            case "testCase":
                queue.Add(await selection.TestCasePayloadAsync(job.Target, null, releaseVersion, limit, cache));
                break;
        }
        job.AddLog($"{queue.Count} to run" + (only is null ? "" : $" (filtered to {only.Count} test case ID(s))"));
        return queue;
    }
}
