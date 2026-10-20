using System.Text.Json;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.History;
using CbBridge.Core.Application.Results;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Infrastructure.Storage;

/// <summary>
/// Run history in SQLite. Opens (and creates/migrates) the database on first use. If the SQLite library is
/// missing, history is switched off with a clear reason in <see cref="Status"/> — runs are never affected.
/// </summary>
public sealed class SqliteResultRepository : IResultRepository, IDisposable
{
    private readonly SqliteDb? _db;

    public bool Available => _db is not null;
    public string Status { get; }

    /// <summary>Each step moves the schema one version up. Never edit an existing step — add a new one.</summary>
    private static readonly string[] Migrations =
    {
        // 1 — runs, results, audit
        """
        CREATE TABLE runs(
            id TEXT PRIMARY KEY, source TEXT NOT NULL, started_by TEXT, label TEXT, selection TEXT, framework_url TEXT,
            started_at TEXT NOT NULL, finished_at TEXT, options_json TEXT, folder TEXT,
            total INTEGER DEFAULT 0, passed INTEGER DEFAULT 0, failed INTEGER DEFAULT 0, blocked INTEGER DEFAULT 0,
            timeout INTEGER DEFAULT 0, error INTEGER DEFAULT 0, not_run INTEGER DEFAULT 0);
        CREATE INDEX ix_runs_started ON runs(started_at);
        CREATE TABLE results(
            run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE, position INTEGER NOT NULL,
            test_key TEXT NOT NULL, item_id TEXT, test_case_id TEXT, testablauf TEXT, name TEXT, is_local INTEGER,
            test_configuration TEXT, verdict TEXT, duration_sec REAL, message TEXT,
            posted_to TEXT, upload TEXT, upload_message TEXT, request_json TEXT, response_json TEXT,
            analysis_state TEXT, analysis_comment TEXT, analysis_json TEXT, comment_upload TEXT, comment_upload_message TEXT,
            feedback INTEGER, feedback_by TEXT, feedback_note TEXT, saved_at TEXT NOT NULL,
            PRIMARY KEY(run_id, position));
        CREATE INDEX ix_results_key ON results(test_key, saved_at);
        CREATE INDEX ix_results_verdict ON results(verdict);
        CREATE TABLE audit(
            id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, actor TEXT, action TEXT, target TEXT,
            outcome TEXT, detail TEXT);
        CREATE INDEX ix_audit_target ON audit(target);
        """
    };

    public SqliteResultRepository(IOptions<DatabaseOptions> options, IHostEnvironment env, ILogger<SqliteResultRepository> logger)
    {
        var o = options.Value;
        if (!o.Enabled) { Status = "switched off (Database:Enabled = false)"; return; }
        var path = System.IO.Path.IsPathRooted(o.Path) ? o.Path : System.IO.Path.Combine(env.ContentRootPath, o.Path);
        try
        {
            SqliteNative.ConfiguredPath = string.IsNullOrWhiteSpace(o.SqliteLibrary) ? null : o.SqliteLibrary;
            _db = new SqliteDb(path);
            var version = Migrate(_db);
            Status = $"SQLite {SqliteDb.LibraryVersion} · schema v{version} · {path}";
            logger.LogInformation("Run history: {Status}", Status);
            if (o.KeepDays > 0) { var n = CleanUp(o.KeepDays); if (n > 0) logger.LogInformation("Deleted {Count} runs older than {Days} days", n, o.KeepDays); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SqliteException or IOException or UnauthorizedAccessException)
        {
            _db?.Dispose();
            _db = null;
            Status = "not available: " + ex.Message;
            logger.LogWarning("Run history is not available: {Reason}", ex.Message);
        }
    }

    private static int Migrate(SqliteDb db)
    {
        db.ExecScript("CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);");
        var current = (int)(db.Scalar<long?>("SELECT MAX(version) FROM schema_version") ?? 0);
        for (var v = current; v < Migrations.Length; v++)
        {
            var step = v;
            db.InTransaction(() =>
            {
                db.ExecScript(Migrations[step]);
                db.Execute("INSERT INTO schema_version(version) VALUES($v)", ("$v", step + 1));
            });
        }
        return Migrations.Length;
    }

    private SqliteDb Db => _db ?? throw new InvalidOperationException("Run history is not available: " + Status);
    private static string Now => DateTimeOffset.UtcNow.ToString("O");
    private static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("O");

    // ---------------------------------------------------------------- writing

    public void SaveRun(RunRecord run, IReadOnlyList<ExecutionResult> board, RunOptions? options)
    {
        int Count(params Verdict[] v) => board.Count(r => v.Contains(r.Verdict));
        Db.Execute("""
            INSERT INTO runs(id, source, started_by, label, selection, framework_url, started_at, finished_at, options_json, folder,
                             total, passed, failed, blocked, timeout, error, not_run)
            VALUES($id, $source, $by, $label, $sel, $fw, $start, $end, $opts, $folder, $total, $p, $f, $b, $t, $e, $n)
            ON CONFLICT(id) DO UPDATE SET finished_at = excluded.finished_at,
                options_json = COALESCE(excluded.options_json, runs.options_json), folder = excluded.folder,
                total = excluded.total, passed = excluded.passed, failed = excluded.failed, blocked = excluded.blocked,
                timeout = excluded.timeout, error = excluded.error, not_run = excluded.not_run
            """,
            ("$id", run.Id), ("$source", run.Source), ("$by", run.StartedBy), ("$label", run.Label),
            ("$sel", run.Selection), ("$fw", run.FrameworkUrl), ("$start", Iso(run.StartedAt)),
            ("$end", run.FinishedAt is { } fin ? Iso(fin) : null),
            ("$opts", options is null ? null : JsonSerializer.Serialize(options)), ("$folder", run.Folder),
            ("$total", board.Count), ("$p", Count(Verdict.Passed)), ("$f", Count(Verdict.Failed)),
            ("$b", Count(Verdict.Blocked)), ("$t", Count(Verdict.Timeout)), ("$e", Count(Verdict.Error)),
            ("$n", Count(Verdict.NotRun, Verdict.Aborted, Verdict.Queued, Verdict.Running)));
    }

    public void SaveResult(RunRecord run, int position, TestRunPayload sent, ExecutionResult r)
    {
        Db.Execute("""
            INSERT OR REPLACE INTO results(run_id, position, test_key, item_id, test_case_id, testablauf, name, is_local,
                test_configuration, verdict, duration_sec, message, posted_to, upload, upload_message, request_json,
                response_json, analysis_state, analysis_comment, analysis_json, comment_upload, comment_upload_message, saved_at)
            VALUES($run, $pos, $key, $item, $tc, $grp, $name, $local, $cfg, $verdict, $dur, $msg, $to, $up, $upmsg,
                   $req, $resp, $as, $ac, $aj, $cu, $cum, $at)
            """,
            ("$run", run.Id), ("$pos", position), ("$key", StoredResult.KeyOf(r)), ("$item", r.ItemId),
            ("$tc", r.TestCaseId), ("$grp", r.GroupName), ("$name", r.Name), ("$local", r.IsLocal),
            ("$cfg", sent.TestConfiguration), ("$verdict", r.Verdict.ToString().ToUpperInvariant()),
            ("$dur", Math.Round(r.Duration.TotalSeconds, 2)), ("$msg", r.Message), ("$to", r.PostTargetId),
            ("$up", r.Posted.ToString()), ("$upmsg", r.PostMessage), ("$req", sent.ToJson()), ("$resp", r.RawJson),
            ("$as", r.AnalysisState == AnalysisState.None ? null : r.AnalysisState.ToString()),
            ("$ac", r.AnalysisComment), ("$aj", r.AnalysisRawJson),
            ("$cu", r.AnalysisPosted.ToString()), ("$cum", r.AnalysisPostMessage), ("$at", Now));
    }

    public void UpdateUpload(string runId, int position, ExecutionResult r) =>
        Db.Execute("UPDATE results SET posted_to = $to, upload = $up, upload_message = $msg WHERE run_id = $run AND position = $pos",
            ("$to", r.PostTargetId), ("$up", r.Posted.ToString()), ("$msg", r.PostMessage), ("$run", runId), ("$pos", position));

    public void UpdateAnalysis(string runId, int position, ExecutionResult r) =>
        Db.Execute("""
            UPDATE results SET analysis_state = $as, analysis_comment = $ac, analysis_json = $aj,
                comment_upload = $cu, comment_upload_message = $cum WHERE run_id = $run AND position = $pos
            """,
            ("$as", r.AnalysisState == AnalysisState.None ? null : r.AnalysisState.ToString()), ("$ac", r.AnalysisComment),
            ("$aj", r.AnalysisRawJson), ("$cu", r.AnalysisPosted.ToString()), ("$cum", r.AnalysisPostMessage),
            ("$run", runId), ("$pos", position));

    public bool SetFeedback(string runId, int position, int? value, string by, string? note) =>
        Db.Execute("UPDATE results SET feedback = $v, feedback_by = $by, feedback_note = $note WHERE run_id = $run AND position = $pos",
            ("$v", value is null ? null : Math.Sign(value.Value)), ("$by", by), ("$note", note), ("$run", runId), ("$pos", position)) > 0;

    public void Audit(AuditEntry e) =>
        Db.Execute("INSERT INTO audit(at, actor, action, target, outcome, detail) VALUES($at, $actor, $action, $target, $outcome, $detail)",
            ("$at", Iso(e.At)), ("$actor", e.Actor), ("$action", e.Action), ("$target", e.Target), ("$outcome", e.Outcome), ("$detail", e.Detail));

    public int CleanUp(int keepDays) =>
        Db.Execute("DELETE FROM runs WHERE started_at < $limit", ("$limit", Iso(DateTimeOffset.UtcNow.AddDays(-keepDays))));

    // ---------------------------------------------------------------- reading

    private const string RunColumns = "id, source, started_by, label, selection, framework_url, started_at, finished_at, total, passed, failed, blocked, timeout, error, not_run, folder, options_json";

    public (IReadOnlyList<RunSummary> Runs, int Total) ListRuns(RunQuery q)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("""
                (selection LIKE $text OR label LIKE $text OR started_by LIKE $text OR id LIKE $text OR framework_url LIKE $text
                 OR id IN (SELECT run_id FROM results WHERE name LIKE $text OR item_id LIKE $text OR test_case_id LIKE $text
                                                     OR test_configuration LIKE $text))
                """);
            args.Add(("$text", "%" + q.Text.Trim() + "%"));
        }
        if (!string.IsNullOrWhiteSpace(q.Source)) { where.Add("source = $source"); args.Add(("$source", q.Source)); }
        if (!string.IsNullOrWhiteSpace(q.Verdict))
        {
            where.Add("id IN (SELECT run_id FROM results WHERE verdict = $verdict)");
            args.Add(("$verdict", q.Verdict.ToUpperInvariant()));
        }
        if (q.From is { } from) { where.Add("started_at >= $from"); args.Add(("$from", Iso(from))); }
        if (q.To is { } to) { where.Add("started_at < $to"); args.Add(("$to", Iso(to))); }
        var filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

        var total = (int)(Db.Scalar<long?>($"SELECT COUNT(*) FROM runs {filter}", args.ToArray()) ?? 0);
        args.Add(("$limit", Math.Clamp(q.Limit, 1, 500)));
        args.Add(("$offset", Math.Max(0, q.Offset)));
        var runs = Db.Query($"SELECT {RunColumns} FROM runs {filter} ORDER BY started_at DESC LIMIT $limit OFFSET $offset",
            MapRun, args.ToArray());
        return (runs, total);
    }

    public RunSummary? GetRun(string runId) =>
        Db.Query($"SELECT {RunColumns} FROM runs WHERE id = $id", MapRun, ("$id", runId)).FirstOrDefault();

    public IReadOnlyList<StoredResult> ResultsOf(string runId) =>
        Db.Query("SELECT * FROM results WHERE run_id = $run ORDER BY position", MapResult, ("$run", runId));

    public StoredResult? GetResult(string runId, int position) =>
        Db.Query("SELECT * FROM results WHERE run_id = $run AND position = $pos", MapResult, ("$run", runId), ("$pos", position)).FirstOrDefault();

    public IReadOnlyList<HistoryPoint> History(string testKey, int limit = 50)
    {
        var keys = testKey.Contains(':') ? new[] { testKey, testKey } : new[] { "tc:" + testKey, "item:" + testKey };
        return Db.Query("""
            SELECT r.run_id, r.position, r.saved_at, r.verdict, r.duration_sec, r.test_configuration, r.analysis_comment,
                   u.source, u.label
            FROM results r JOIN runs u ON u.id = r.run_id
            WHERE r.test_key IN ($k1, $k2) OR (r.test_key LIKE 'item:%' AND r.test_case_id = $bare)
            ORDER BY r.saved_at DESC LIMIT $limit
            """,
            row => new HistoryPoint(row.Str("run_id")!, (int)row.Long("position"), row.Date("saved_at") ?? default,
                row.Str("verdict") ?? "", row.Double("duration_sec"), row.Str("test_configuration"),
                row.Str("source") ?? "", row.Str("label"), row.Str("analysis_comment")),
            ("$k1", keys[0]), ("$k2", keys[1]), ("$bare", testKey.Contains(':') ? testKey[(testKey.IndexOf(':') + 1)..] : testKey),
            ("$limit", Math.Clamp(limit, 1, 1000)));
    }

    public StoredResult? Previous(StoredResult r) =>
        Db.Query("""
            SELECT * FROM results WHERE test_key = $key AND saved_at < $at AND run_id <> $run
              AND verdict NOT IN ('NOTRUN', 'ABORTED', 'QUEUED')
            ORDER BY saved_at DESC LIMIT 1
            """, MapResult, ("$key", r.TestKey), ("$at", Iso(r.SavedAt)), ("$run", r.RunId)).FirstOrDefault();

    public StoredResult? LastPassingBefore(StoredResult r) =>
        Db.Query("SELECT * FROM results WHERE test_key = $key AND saved_at < $at AND verdict = 'PASSED' ORDER BY saved_at DESC LIMIT 1",
            MapResult, ("$key", r.TestKey), ("$at", Iso(r.SavedAt))).FirstOrDefault();

    public IReadOnlyList<AuditEntry> AuditLog(int limit = 200, string? target = null) =>
        Db.Query($"SELECT * FROM audit {(target is null ? "" : "WHERE target = $target")} ORDER BY id DESC LIMIT $limit",
            row => new AuditEntry(row.Date("at") ?? default, row.Str("actor") ?? "", row.Str("action") ?? "",
                row.Str("target") ?? "", row.Str("outcome") ?? "", row.Str("detail")),
            ("$target", target), ("$limit", Math.Clamp(limit, 1, 5000)));

    private static RunSummary MapRun(SqliteRow r) => new(
        r.Str("id")!, r.Str("source") ?? "", r.Str("started_by") ?? "", r.Str("label"), r.Str("selection") ?? "",
        r.Str("framework_url") ?? "", r.Date("started_at") ?? default, r.Date("finished_at"),
        (int)r.Long("total"), (int)r.Long("passed"), (int)r.Long("failed"), (int)r.Long("blocked"),
        (int)r.Long("timeout"), (int)r.Long("error"), (int)r.Long("not_run"), r.Str("folder"), r.Str("options_json"));

    private static StoredResult MapResult(SqliteRow r) => new()
    {
        RunId = r.Str("run_id")!, Position = (int)r.Long("position"), TestKey = r.Str("test_key") ?? "",
        ItemId = r.Str("item_id") ?? "", TestCaseId = r.Str("test_case_id"), Testablauf = r.Str("testablauf"),
        Name = r.Str("name") ?? "", IsLocal = r.Bool("is_local"), TestConfiguration = r.Str("test_configuration"),
        Verdict = r.Str("verdict") ?? "", DurationSec = r.Double("duration_sec"), Message = r.Str("message") ?? "",
        PostedTo = r.Str("posted_to"), Upload = r.Str("upload") ?? "", UploadMessage = r.Str("upload_message"),
        RequestJson = r.Str("request_json"), ResponseJson = r.Str("response_json"),
        AnalysisState = r.Str("analysis_state"), AnalysisComment = r.Str("analysis_comment"), AnalysisJson = r.Str("analysis_json"),
        CommentUpload = r.Str("comment_upload"), CommentUploadMessage = r.Str("comment_upload_message"),
        Feedback = (int?)r.LongOrNull("feedback"), FeedbackBy = r.Str("feedback_by"), FeedbackNote = r.Str("feedback_note"),
        SavedAt = r.Date("saved_at") ?? default
    };

    public void Dispose() => _db?.Dispose();
}
