namespace CbBridge.Core.Domain;

public enum Verdict { Queued, Running, Passed, Failed, Blocked, Timeout, Aborted, Error, NotRun }

public enum PostState { NotPosted, Posted, DryRun, Failed }

/// <summary>Result of one test run as the bridge sees it (framework JSON + bridge-side outcome).</summary>
public sealed class ExecutionResult
{
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public Verdict Verdict { get; set; } = Verdict.Queued;
    public TimeSpan Duration { get; set; }
    public string Message { get; set; } = "";

    /// <summary>The framework's JSON exactly as returned (null when it never answered).</summary>
    public string? RawJson { get; set; }

    public PostState Posted { get; set; } = PostState.NotPosted;
    public string? PostMessage { get; set; }

    /// <summary>A test from "My tests" — it has no codebeamer item to post to.</summary>
    public bool IsLocal { get; set; }

    /// <summary>Run multiple: the test case this result belongs to (null for ordinary test runs).</summary>
    public string? TestCaseId { get; set; }

    /// <summary>Run multiple: the Testablauf the test case was picked from.</summary>
    public string? GroupName { get; set; }

    /// <summary>
    /// The codebeamer test run the result is posted to: the test run itself, or the new run created for a
    /// test case. Null = nowhere to post (local test, or no run was created).
    /// </summary>
    public string? PostTargetId { get; set; }

    /// <summary>Only real outcomes with a target test run are posted; aborted / not-run / errors / local tests are not.</summary>
    public bool CanPost => !IsLocal && PostTargetId is not null
                           && Verdict is Verdict.Passed or Verdict.Failed or Verdict.Blocked or Verdict.Timeout;

    /// <summary>Board entry for a queued payload: ordinary test runs post to themselves, test cases only to the run
    /// created for them at execution, local tests nowhere.</summary>
    public static ExecutionResult ForPayload(TestRunPayload p) => new()
    {
        ItemId = p.ItemId,
        Name = p.Name,
        IsLocal = p.IsLocal,
        TestCaseId = p.SourceTestCaseId,
        GroupName = p.GroupName,
        PostTargetId = p.IsLocal || p.SourceTestCaseId is not null ? null : p.ItemId
    };

    public void CopyOutcomeFrom(ExecutionResult other)
    {
        Verdict = other.Verdict;
        Duration = other.Duration;
        Message = other.Message;
        RawJson = other.RawJson;
    }
}

/// <summary>Settings chosen in the UI for one run.</summary>
public sealed class RunOptions
{
    public int DefaultTimeLimitSec { get; set; } = 60;
    public int OverallLimitSec { get; set; } = 1800;
    public bool PostBack { get; set; } = true;
    public bool Continuous { get; set; }

    /// <summary>Run multiple: create a new codebeamer test run for each test case just before it runs.</summary>
    public bool CreateNewRuns { get; set; } = true;

    public RunOptions Clone() => (RunOptions)MemberwiseClone();
}
