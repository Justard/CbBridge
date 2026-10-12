namespace CbBridge.Web.Domain;

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

    /// <summary>Only real outcomes of codebeamer tests are posted; aborted / not-run / errors / local tests are not.</summary>
    public bool CanPost => !IsLocal && Verdict is Verdict.Passed or Verdict.Failed or Verdict.Blocked or Verdict.Timeout;

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

    public RunOptions Clone() => (RunOptions)MemberwiseClone();
}
