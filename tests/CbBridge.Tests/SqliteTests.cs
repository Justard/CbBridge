using CbBridge.Core.Application.History;
using CbBridge.Core.Application.Results;
using CbBridge.Core.Domain;
using CbBridge.Core.Infrastructure.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CbBridge.Tests;

/// <summary>The run history against the operating system's real SQLite, in a temporary folder.</summary>
public sealed class SqliteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cbbridge-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private SqliteResultRepository Open(int keepDays = 0) =>
        new(Options.Create(new DatabaseOptions { Path = Path.Combine(_dir, "t.db"), KeepDays = keepDays }), new Env(_dir),
            NullLogger<SqliteResultRepository>.Instance);

    private static RunRecord Run(string by = "jdoe") => new() { Source = "console", StartedBy = by, Selection = "#88132", FrameworkUrl = "http://bench" };

    private static (TestRunPayload, ExecutionResult) Test(Verdict v, string itemId = "88132", string? tc = null, string cfg = "R4.3 / FW 1") =>
        (new TestRunPayload { ItemId = itemId, Name = "BT", TestConfiguration = cfg, SourceTestCaseId = tc },
         new ExecutionResult { ItemId = itemId, Name = "BT", Verdict = v, TestCaseId = tc, RawJson = $"{{\"verdict\":\"{v}\"}}", Duration = TimeSpan.FromSeconds(2) });

    private static void Save(SqliteResultRepository repo, RunRecord run, params (TestRunPayload P, ExecutionResult R)[] tests)
    {
        repo.SaveRun(run, Array.Empty<ExecutionResult>(), null);
        for (var i = 0; i < tests.Length; i++) repo.SaveResult(run, i + 1, tests[i].P, tests[i].R);
        run.FinishedAt = DateTimeOffset.Now;
        repo.SaveRun(run, tests.Select(t => t.R).ToList(), new RunOptions());
        Thread.Sleep(5);   // distinct timestamps
    }

    [Test]
    public void OpensWithTheSystemSqlite()
    {
        using var repo = Open();
        Assert.True(repo.Available, repo.Status);
        Assert.Contains("schema v1", repo.Status);
    }

    [Test]
    public void ReopeningDoesNotMigrateTwice()
    {
        using (var first = Open()) Save(first, Run(), Test(Verdict.Passed));
        using var second = Open();
        Assert.True(second.Available, second.Status);
        Assert.Equal(1, second.ListRuns(new RunQuery()).Total, "data kept");
    }

    [Test]
    public void RunCountsAndSearch()
    {
        using var repo = Open();
        Save(repo, Run(), Test(Verdict.Passed), Test(Verdict.Failed, "88133"));
        var run = repo.ListRuns(new RunQuery()).Runs.Single();
        Assert.Equal(2, run.Total);
        Assert.Equal(1, run.Passed);
        Assert.Equal(1, run.Failed);
        Assert.Equal(1, repo.ListRuns(new RunQuery(Verdict: "FAILED")).Total, "verdict filter");
        Assert.Equal(1, repo.ListRuns(new RunQuery(Text: "FW 1")).Total, "search in test configuration");
        Assert.Equal(0, repo.ListRuns(new RunQuery(Text: "nothing-like-this")).Total);
        Assert.Equal(0, repo.ListRuns(new RunQuery(Source: "ci")).Total, "source filter");
    }

    [Test]
    public void PreviousAndLastPassing()
    {
        using var repo = Open();
        Save(repo, Run(), Test(Verdict.Passed, cfg: "FW 1"));
        Save(repo, Run(), Test(Verdict.Failed, cfg: "FW 2"));
        var latest = Run();
        Save(repo, latest, Test(Verdict.Failed, cfg: "FW 3"));
        var now = repo.GetResult(latest.Id, 1)!;
        Assert.Equal("FAILED", repo.Previous(now)?.Verdict, "previous");
        Assert.Equal("FW 2", repo.Previous(now)?.TestConfiguration);
        Assert.Equal("FW 1", repo.LastPassingBefore(now)?.TestConfiguration, "last pass");
        Assert.Equal(3, repo.History("item:88132").Count);
        Assert.Equal(3, repo.History("88132").Count, "bare id");
    }

    [Test]
    public void TestCasesAreTrackedAcrossNewTestRuns()
    {
        using var repo = Open();
        Save(repo, Run(), Test(Verdict.Passed, itemId: "90001", tc: "10232"));
        var second = Run();
        Save(repo, second, Test(Verdict.Failed, itemId: "90002", tc: "10232"));
        var now = repo.GetResult(second.Id, 1)!;
        Assert.Equal("tc:10232", now.TestKey);
        Assert.Equal("PASSED", repo.Previous(now)?.Verdict, "same test case, different test run IDs");
    }

    [Test]
    public void UploadAnalysisFeedbackAudit()
    {
        using var repo = Open();
        var run = Run();
        var (p, r) = Test(Verdict.Failed);
        Save(repo, run, (p, r));
        r.Posted = PostState.Posted; r.PostTargetId = "88132"; r.PostMessage = "PUT → 200";
        repo.UpdateUpload(run.Id, 1, r);
        r.AnalysisState = AnalysisState.Done; r.AnalysisComment = "look at step 2"; r.AnalysisRawJson = """{"comment":"look at step 2"}""";
        repo.UpdateAnalysis(run.Id, 1, r);
        Assert.True(repo.SetFeedback(run.Id, 1, -1, "jdoe", "wrong step"));
        Assert.False(repo.SetFeedback(run.Id, 9, 1, "jdoe", null), "unknown position");
        repo.Audit(new AuditEntry(DateTimeOffset.UtcNow, "jdoe", "post result FAILED", "item:88132", "Posted", "PUT → 200"));

        var stored = repo.GetResult(run.Id, 1)!;
        Assert.Equal("Posted", stored.Upload);
        Assert.Equal("look at step 2", stored.AnalysisComment);
        Assert.Equal(-1, stored.Feedback);
        Assert.Equal("wrong step", stored.FeedbackNote);
        Assert.Equal("item:88132", repo.AuditLog(10, "item:88132").Single().Target);
    }

    [Test]
    public void TextWithQuotesAndUnicodeIsStoredExactly()
    {
        using var repo = Open();
        var run = Run("O'Brien \"Ü\" 日本");
        var (p, r) = Test(Verdict.Failed);
        r.Message = "Fehler: Schritt 2 – 'ß' → ✓";
        Save(repo, run, (p, r));
        Assert.Equal("O'Brien \"Ü\" 日本", repo.GetRun(run.Id)!.StartedBy);
        Assert.Equal(r.Message, repo.GetResult(run.Id, 1)!.Message);
    }

    [Test]
    public void CleanUpDeletesOldRunsWithTheirResults()
    {
        using var repo = Open();
        var old = new RunRecord { Source = "ci", Selection = "old" };
        typeof(RunRecord).GetField("<StartedAt>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(old, DateTimeOffset.UtcNow.AddDays(-400));
        Save(repo, old, Test(Verdict.Passed));
        Save(repo, Run(), Test(Verdict.Passed));
        Assert.Equal(1, repo.CleanUp(365), "deleted");
        Assert.Equal(1, repo.ListRuns(new RunQuery()).Total);
        Assert.Equal(0, repo.ResultsOf(old.Id).Count, "results cascade");
    }

    [Test]
    public void ParallelWritesAreSafe()
    {
        using var repo = Open();
        Parallel.For(0, 40, i => Save(repo, Run(), Test(i % 2 == 0 ? Verdict.Passed : Verdict.Failed)));
        Assert.Equal(40, repo.ListRuns(new RunQuery(Limit: 500)).Total);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
