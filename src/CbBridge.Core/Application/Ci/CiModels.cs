using System.Text.Json.Serialization;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Ci;

/// <summary>appsettings.json → "Ci": the API that Jenkins (or any CI) uses to start runs.</summary>
public sealed class CiOptions
{
    /// <summary>Off by default: the API answers 404 until an admin enables it.</summary>
    public bool Enabled { get; set; }

    /// <summary>Shared secret Jenkins sends in the X-Api-Key header. Required when Enabled.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Finished jobs kept in memory for status / JUnit download.</summary>
    public int KeepJobs { get; set; } = 100;

    public int DefaultTimeLimitSec { get; set; } = 120;
    public int DefaultOverallLimitSec { get; set; } = 3600;

    /// <summary>AI analysis: after the last test, the job waits this long for open analyses (for the JUnit report).</summary>
    public int AnalysisWaitSec { get; set; } = 300;
}

/// <summary>What Jenkins asks for. Exactly one of Category / Testablauf / TestRun / TestCase (ID or link).</summary>
public sealed class CiRunRequest
{
    /// <summary>codebeamer address; default Codebeamer:DefaultBaseUrl.</summary>
    public string? CodebeamerUrl { get; set; }

    /// <summary>Category holding Testabläufe → all their test cases (Run multiple).</summary>
    public string? Category { get; set; }
    /// <summary>One Testablauf (test run / test set) → its test cases.</summary>
    public string? Testablauf { get; set; }
    /// <summary>One existing test run → run it, post to it.</summary>
    public string? TestRun { get; set; }
    /// <summary>One test case → run it (new test run if CreateNewRuns).</summary>
    public string? TestCase { get; set; }

    /// <summary>Optional: only these test case IDs from the category / Testablauf.</summary>
    public List<string>? OnlyTestCases { get; set; }

    public string? Release { get; set; }
    public List<string>? Versions { get; set; }

    public bool CreateNewRuns { get; set; } = true;
    public bool PostBack { get; set; } = true;

    /// <summary>Send failed tests to the AI agent (needs Analysis:Enabled on the bridge).</summary>
    public bool Analyze { get; set; }

    /// <summary>Test system (framework address); default Framework:BaseUrl.</summary>
    public string? FrameworkUrl { get; set; }
    public int? TimeLimitSec { get; set; }
    public int? OverallLimitSec { get; set; }

    /// <summary>Free text shown in logs, e.g. "Jenkins nightly #42".</summary>
    public string? Label { get; set; }

    public (string Kind, string Value)? Selector()
    {
        var set = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(Category)) set.Add(("category", Category));
        if (!string.IsNullOrWhiteSpace(Testablauf)) set.Add(("testablauf", Testablauf));
        if (!string.IsNullOrWhiteSpace(TestRun)) set.Add(("testRun", TestRun));
        if (!string.IsNullOrWhiteSpace(TestCase)) set.Add(("testCase", TestCase));
        return set.Count == 1 ? set[0] : null;
    }
}

public enum CiJobState { Queued, Running, Finished, Failed, Aborted }

/// <summary>One CI-triggered run, kept in memory.</summary>
public sealed class CiJob
{
    private readonly object _lock = new();
    private readonly List<string> _log = new();

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Kind { get; init; }
    public required string Target { get; init; }
    public required string FrameworkUrl { get; init; }
    public string? Label { get; init; }
    public string RequestedBy { get; init; } = "";
    /// <summary>The run's ID in the history (/history/{RunId}, /api/v1/runs/{RunId}).</summary>
    public string? RunId { get; set; }
    /// <summary>Which API client started it.</summary>
    public string? Client { get; set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public CiJobState State { get; set; } = CiJobState.Queued;
    public string? Error { get; set; }
    public List<ExecutionResult> Results { get; } = new();
    public CancellationTokenSource Abort { get; } = new();

    public void AddLog(string line)
    {
        lock (_lock) _log.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
    }

    public List<string> LogSnapshot(int from = 0)
    {
        lock (_lock) return _log.Skip(Math.Max(0, from)).ToList();
    }

    public int LogCount
    {
        get { lock (_lock) return _log.Count; }
    }

    public bool IsDone => State is CiJobState.Finished or CiJobState.Failed or CiJobState.Aborted;
}

/// <summary>JSON answer for status calls.</summary>
public sealed record CiJobView(
    string Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CiJobState State,
    string Kind, string Target, string FrameworkUrl, string? Label,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string? Error, CiSummary Summary, List<CiResultView> Results, int LogFrom, List<string> Log, string? RunId)
{
    public static CiJobView From(CiJob job, int logFrom = 0)
    {
        var results = job.Results.ToList().Select(CiResultView.From).ToList();
        return new CiJobView(job.Id, job.State, job.Kind, job.Target, job.FrameworkUrl, job.Label,
            job.CreatedAt, job.StartedAt, job.FinishedAt, job.Error, CiSummary.From(job.Results.ToList()),
            results, logFrom, job.LogSnapshot(logFrom), job.RunId);
    }
}

public sealed record CiSummary(int Total, int Passed, int Failed, int Blocked, int Timeout, int Error, int NotRun, int Aborted, bool AllPassed)
{
    public static CiSummary From(List<ExecutionResult> r) => new(
        r.Count,
        r.Count(x => x.Verdict == Verdict.Passed),
        r.Count(x => x.Verdict == Verdict.Failed),
        r.Count(x => x.Verdict == Verdict.Blocked),
        r.Count(x => x.Verdict == Verdict.Timeout),
        r.Count(x => x.Verdict == Verdict.Error),
        r.Count(x => x.Verdict == Verdict.NotRun),
        r.Count(x => x.Verdict == Verdict.Aborted),
        r.Count > 0 && r.All(x => x.Verdict == Verdict.Passed));
}

public sealed record CiResultView(string ItemId, string? TestCaseId, string Name, string Verdict, double DurationSec,
    string Message, string? PostedTo, string Upload, string? Analysis, string? AnalysisComment)
{
    public static CiResultView From(ExecutionResult r) => new(
        r.ItemId, r.TestCaseId, r.Name, r.Verdict.ToString().ToUpperInvariant(), Math.Round(r.Duration.TotalSeconds, 2),
        r.Message, r.PostTargetId, r.Posted.ToString(),
        r.AnalysisState == AnalysisState.None ? null : r.AnalysisState.ToString(), r.AnalysisComment);
}
