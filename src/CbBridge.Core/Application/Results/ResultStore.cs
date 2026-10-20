using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Results;

/// <summary>appsettings.json → "Results": every result is also written to disk on the bridge server.</summary>
public sealed class ResultStoreOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Relative to the app folder, or an absolute path (e.g. D:\BridgeResults or /var/lib/cbbridge/results).</summary>
    public string Folder { get; set; } = "results";
    /// <summary>Day folders older than this are deleted (0 = keep forever).</summary>
    public int KeepDays { get; set; } = 90;
}

/// <summary>Who started a run and what it was — written as run.json next to the results.</summary>
public sealed class RunRecord
{
    public string Id { get; } = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>"console" or "ci".</summary>
    public required string Source { get; init; }
    public string StartedBy { get; init; } = "";
    public string Selection { get; init; } = "";
    public string FrameworkUrl { get; init; } = "";
    public string? Label { get; init; }
    /// <summary>Set by the store: where this run's files are.</summary>
    public string? Folder { get; set; }
}

/// <summary>
/// Writes results/{yyyy-MM-dd}/{run id}/run.json plus one {itemId}.json per test: the exact request sent to the
/// framework, its full answer, the verdict and where it was posted. Never throws — a disk problem is logged and
/// the run goes on.
/// </summary>
public sealed partial class ResultStore(IOptions<ResultStoreOptions> options, IHostEnvironment env, ILogger<ResultStore> logger)
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool Enabled => options.Value.Enabled;

    private string Root => Path.IsPathRooted(options.Value.Folder)
        ? options.Value.Folder
        : Path.Combine(env.ContentRootPath, options.Value.Folder);

    /// <summary>Creates the run folder (and cleans up old day folders). Returns the folder, or null when disabled / failed.</summary>
    public string? Begin(RunRecord run)
    {
        if (!Enabled) return null;
        try
        {
            var folder = Path.Combine(Root, run.StartedAt.ToString("yyyy-MM-dd"), run.Id);
            Directory.CreateDirectory(folder);
            run.Folder = folder;
            CleanUp();
            return folder;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create the results folder under {Root}", Root);
            return null;
        }
    }

    /// <summary>One test: request, framework answer, verdict, upload state.</summary>
    /// <param name="position">1-based place in the run — part of the file name, so a test that runs twice
    /// (same test case in two Testabläufe) gets two files.</param>
    public async Task SaveResultAsync(RunRecord run, int position, TestRunPayload sent, ExecutionResult result)
    {
        if (run.Folder is null) return;
        var doc = new JsonObject
        {
            ["run"] = run.Id,
            ["itemId"] = result.ItemId,
            ["testCaseId"] = result.TestCaseId,
            ["testablauf"] = result.GroupName,
            ["name"] = result.Name,
            ["verdict"] = result.Verdict.ToString().ToUpperInvariant(),
            ["durationSec"] = Math.Round(result.Duration.TotalSeconds, 2),
            ["message"] = result.Message,
            ["postedTo"] = result.PostTargetId,
            ["upload"] = result.Posted.ToString(),
            ["uploadMessage"] = result.PostMessage,
            ["analysis"] = AnalysisNode(result),
            ["savedAt"] = DateTimeOffset.Now.ToString("O"),
            ["request"] = sent.ToJsonObject(),
            ["response"] = ParseOrText(result.RawJson)
        };
        await WriteAsync(Path.Combine(run.Folder, FileName(position, result)), doc.ToJsonString(Pretty));
    }

    /// <summary>run.json: who, when, what, summary and one line per test. Called at the end and after later uploads.</summary>
    public async Task SaveRunAsync(RunRecord run, IEnumerable<ExecutionResult> board, RunOptions? runOptions = null)
    {
        if (run.Folder is null) return;
        var results = board.ToList();
        var doc = new JsonObject
        {
            ["id"] = run.Id,
            ["source"] = run.Source,
            ["startedBy"] = run.StartedBy,
            ["label"] = run.Label,
            ["selection"] = run.Selection,
            ["frameworkUrl"] = run.FrameworkUrl,
            ["startedAt"] = run.StartedAt.ToString("O"),
            ["finishedAt"] = run.FinishedAt?.ToString("O"),
            ["options"] = runOptions is null ? null : JsonSerializer.SerializeToNode(runOptions),
            ["summary"] = new JsonObject
            {
                ["total"] = results.Count,
                ["passed"] = results.Count(r => r.Verdict == Verdict.Passed),
                ["failed"] = results.Count(r => r.Verdict == Verdict.Failed),
                ["timeout"] = results.Count(r => r.Verdict == Verdict.Timeout),
                ["error"] = results.Count(r => r.Verdict == Verdict.Error),
                ["notRun"] = results.Count(r => r.Verdict is Verdict.NotRun or Verdict.Aborted)
            },
            ["results"] = new JsonArray(results.Select((r, i) => (JsonNode?)new JsonObject
            {
                ["position"] = i + 1,
                ["itemId"] = r.ItemId,
                ["testCaseId"] = r.TestCaseId,
                ["name"] = r.Name,
                ["verdict"] = r.Verdict.ToString().ToUpperInvariant(),
                ["durationSec"] = Math.Round(r.Duration.TotalSeconds, 2),
                ["postedTo"] = r.PostTargetId,
                ["upload"] = r.Posted.ToString(),
                ["analysis"] = r.AnalysisState == AnalysisState.None ? null : r.AnalysisState.ToString(),
                ["file"] = r.Verdict is Verdict.Queued or Verdict.NotRun ? null : FileName(i + 1, r)
            }).ToArray())
        };
        await WriteAsync(Path.Combine(run.Folder, "run.json"), doc.ToJsonString(Pretty));
    }

    /// <summary>After a later upload: refresh the upload state in the test's file.</summary>
    public async Task UpdateUploadAsync(RunRecord run, int position, ExecutionResult result)
    {
        if (run.Folder is null) return;
        var path = Path.Combine(run.Folder, FileName(position, result));
        try
        {
            if (!File.Exists(path)) return;
            if (JsonNode.Parse(await File.ReadAllTextAsync(path)) is not JsonObject doc) return;
            doc["postedTo"] = result.PostTargetId;
            doc["upload"] = result.Posted.ToString();
            doc["uploadMessage"] = result.PostMessage;
            await WriteAsync(path, doc.ToJsonString(Pretty));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not update {Path}", path);
        }
    }

    /// <summary>When the AI analysis arrives (or gives up): write it into the test's file.</summary>
    public async Task UpdateAnalysisAsync(RunRecord run, int position, ExecutionResult result)
    {
        if (run.Folder is null) return;
        var path = Path.Combine(run.Folder, FileName(position, result));
        try
        {
            if (!File.Exists(path)) return;
            if (JsonNode.Parse(await File.ReadAllTextAsync(path)) is not JsonObject doc) return;
            doc["analysis"] = AnalysisNode(result);
            await WriteAsync(path, doc.ToJsonString(Pretty));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not update {Path}", path);
        }
    }

    private static JsonObject? AnalysisNode(ExecutionResult r) => r.AnalysisState == AnalysisState.None ? null : new JsonObject
    {
        ["state"] = r.AnalysisState.ToString(),
        ["comment"] = r.AnalysisComment,
        ["agentAnswer"] = ParseOrText(r.AnalysisRawJson),
        ["commentUpload"] = r.AnalysisPosted.ToString(),
        ["commentUploadMessage"] = r.AnalysisPostMessage
    };

    /// <summary>"01_88131.json", "02_90001_TC-10232.json" — run order first, only safe characters.</summary>
    private static string FileName(int position, ExecutionResult r)
    {
        var name = $"{position:00}_" + Safe(r.ItemId);
        if (r.TestCaseId is not null && r.TestCaseId != r.ItemId) name += "_TC-" + Safe(r.TestCaseId);
        return name + ".json";
    }

    private static string Safe(string s)
    {
        var cleaned = UnsafeChars().Replace(s, "_");
        return cleaned.Length == 0 ? "unknown" : cleaned[..Math.Min(cleaned.Length, 80)];
    }

    private static JsonNode? ParseOrText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonNode.Parse(raw); }
        catch (JsonException) { return JsonValue.Create(raw); }
    }

    private async Task WriteAsync(string path, string content)
    {
        try
        {
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, content);
            File.Move(tmp, path, overwrite: true);   // never leave a half-written file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Path}", path);
        }
    }

    /// <summary>Deletes day folders (yyyy-MM-dd) older than KeepDays.</summary>
    private void CleanUp()
    {
        var keep = options.Value.KeepDays;
        if (keep <= 0 || !Directory.Exists(Root)) return;
        var limit = DateTime.Today.AddDays(-keep);
        foreach (var dir in Directory.GetDirectories(Root))
        {
            if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                && day < limit)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not delete old results {Dir}", dir); }
            }
        }
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")] private static partial Regex UnsafeChars();
}
