using CbBridge.Core.Application.Results;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.History;

/// <summary>appsettings.json → "Database": run history in a SQLite file (the OS's own SQLite, no package).</summary>
public sealed class DatabaseOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Relative to the app folder, or absolute.</summary>
    public string Path { get; set; } = "data/cbbridge.db";
    /// <summary>Optional: a specific SQLite library file. Empty = winsqlite3 / libsqlite3 from the system.</summary>
    public string SqliteLibrary { get; set; } = "";
    /// <summary>Runs older than this are deleted from the database (0 = keep forever).</summary>
    public int KeepDays { get; set; } = 365;
}

public sealed record RunSummary(
    string Id, string Source, string StartedBy, string? Label, string Selection, string FrameworkUrl,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int Total, int Passed, int Failed, int Blocked, int Timeout, int Error, int NotRun,
    string? Folder, string? OptionsJson)
{
    public bool IsFinished => FinishedAt is not null;
    public int Problems => Failed + Blocked + Timeout + Error;
}

/// <summary>One test of one run, as stored.</summary>
public sealed class StoredResult
{
    public string RunId { get; init; } = "";
    public int Position { get; init; }
    /// <summary>"tc:10232", "item:88131" or "local:local-4ef446" — what "the same test" means across runs.</summary>
    public string TestKey { get; init; } = "";
    public string ItemId { get; init; } = "";
    public string? TestCaseId { get; init; }
    public string? Testablauf { get; init; }
    public string Name { get; init; } = "";
    public bool IsLocal { get; init; }
    public string? TestConfiguration { get; init; }
    public string Verdict { get; init; } = "";
    public double DurationSec { get; init; }
    public string Message { get; init; } = "";
    public string? PostedTo { get; init; }
    public string Upload { get; init; } = "";
    public string? UploadMessage { get; init; }
    public string? RequestJson { get; init; }
    public string? ResponseJson { get; init; }
    public string? AnalysisState { get; init; }
    public string? AnalysisComment { get; init; }
    public string? AnalysisJson { get; init; }
    public string? CommentUpload { get; init; }
    public string? CommentUploadMessage { get; init; }
    /// <summary>Feedback on the AI comment: +1 helpful, -1 not helpful, null none.</summary>
    public int? Feedback { get; init; }
    public string? FeedbackBy { get; init; }
    public string? FeedbackNote { get; init; }
    public DateTimeOffset SavedAt { get; init; }

    /// <summary>"posted", "dry run", "failed", "—".</summary>
    public string UploadLabel => Upload switch { "Posted" => "posted", "DryRun" => "dry run", "Failed" => "failed", _ => "—" };

    public bool IsProblem => Verdict is "FAILED" or "TIMEOUT" or "ERROR" or "BLOCKED";

    public static string KeyOf(ExecutionResult r) =>
        r.IsLocal ? "local:" + r.ItemId : r.TestCaseId is not null ? "tc:" + r.TestCaseId : "item:" + r.ItemId;
}

public sealed record RunQuery(
    string? Text = null, string? Source = null, string? Verdict = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 50, int Offset = 0);

/// <summary>One earlier execution of a test, for history and comparison.</summary>
public sealed record HistoryPoint(
    string RunId, int Position, DateTimeOffset At, string Verdict, double DurationSec,
    string? TestConfiguration, string Source, string? Label, string? AnalysisComment);

/// <summary>Who wrote what to codebeamer (or changed something in the bridge), and with which outcome.</summary>
public sealed record AuditEntry(DateTimeOffset At, string Actor, string Action, string Target, string Outcome, string? Detail);
