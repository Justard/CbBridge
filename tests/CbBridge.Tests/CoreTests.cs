using System.Text.Json.Nodes;
using CbBridge.Core.Application;
using CbBridge.Core.Application.Analysis;
using CbBridge.Core.Application.Api;
using CbBridge.Core.Application.Ci;
using CbBridge.Core.Application.Editing;
using CbBridge.Core.Application.History;
using CbBridge.Core.Application.Links;
using CbBridge.Core.Domain;
using CbBridge.Core.Infrastructure.Events;
using Microsoft.Extensions.Options;

namespace CbBridge.Tests;

public static class LinkParserTests
{
    [Test] public static void DigitsAreAnItem() => Assert.Equal(new { K = LinkKind.Item, Id = (string?)"88132" }, Of("88132"));
    [Test] public static void ItemLink() => Assert.Equal(new { K = LinkKind.Item, Id = (string?)"10231" }, Of("https://cb.example.com/cb/item/10231"));
    [Test] public static void IssueLink() => Assert.Equal(new { K = LinkKind.Item, Id = (string?)"10231" }, Of("https://cb.example.com/cb/issue/10231?orgDitchnetTabPaneId=x"));
    [Test] public static void CategoryLink() => Assert.Equal(new { K = LinkKind.Category, Id = (string?)"4471" }, Of("https://cb.example.com/cb/category/4471"));
    [Test] public static void TrackerLink() => Assert.Equal(new { K = LinkKind.Category, Id = (string?)"4471" }, Of("/cb/tracker/4471"));
    [Test] public static void ProjectLink() => Assert.Equal(new { K = LinkKind.Project, Id = (string?)"29" }, Of("https://cb.example.com/cb/project/29"));
    [Test] public static void GarbageIsUnknown() => Assert.Equal(LinkKind.Unknown, LinkParser.Parse("hello world").Kind);

    private static object Of(string s) { var l = LinkParser.Parse(s); return new { K = l.Kind, l.Id }; }
}

public static class PayloadTests
{
    private const string Item = """
        {"description":"Pair five phones in sequence.","modifier":{"name":"jdoe","uri":"/user/7"},
         "name":"Bluetooth pairing with 5 devices","parameters":{"stepDelaySec":1},
         "parent":{"name":"Release gate 4.3","uri":"/item/88130"},
         "releases":[{"name":"R4.3","uri":"/item/7001"}],
         "testSteps":[["Enable pairing","Head unit visible",true],["Pair 5 devices","5 paired",false]],
         "uri":"/item/88132","versions":[{"name":"FW 4.3.0-rc2","uri":"/item/7103"},{"name":"HW C2","uri":"/item/7102"}]}
        """;

    private static PayloadBuilder Builder() => new(Options.Create(new CodebeamerOptions()));

    [Test]
    public static void ExtractsTheFieldsTheFrameworkNeeds()
    {
        var p = Builder().FromItem(JsonNode.Parse(Item)!, 90);
        Assert.Equal("88132", p.ItemId, "itemId from uri");
        Assert.Equal("Bluetooth pairing with 5 devices", p.Name);
        Assert.Equal("jdoe", p.Modifier);
        Assert.Equal(90, p.TimeLimitSec);
        Assert.Equal(2, (p.TestRunSteps as JsonArray)?.Count ?? 0, "steps");
    }

    [Test]
    public static void TestConfigurationFromReleaseAndVersions() =>
        Assert.Equal("R4.3 / FW 4.3.0-rc2, HW C2", Builder().FromItem(JsonNode.Parse(Item)!, 60).TestConfiguration);

    [Test]
    public static void ExtrasSurviveTheJsonRoundTripAsTopLevelKeys()
    {
        var p = Builder().FromItem(JsonNode.Parse(Item)!, 60);
        var json = JsonNode.Parse(p.ToJson())!.AsObject();
        Assert.True(json["releases"] is JsonArray, "releases at top level");
        Assert.Null(json["Extra"], "no nested Extra");
        var back = TestRunPayload.FromJson(p.ToJson());
        Assert.Equal(p.TestConfiguration, back.TestConfiguration);
        Assert.True(back.Extra?["versions"] is JsonArray, "versions back in Extra");
    }

    [Test]
    public static void WithTimeLimitKeepsBridgeOnlyFlags()
    {
        var p = new TestRunPayload { ItemId = "1", SourceTestCaseId = "10232", GroupName = "TA", IsLocal = false, Analyze = false };
        var q = p.WithTimeLimit(5);
        Assert.Equal(5, q.TimeLimitSec);
        Assert.Equal("10232", q.SourceTestCaseId);
        Assert.False(q.Analyze, "Analyze flag kept");
    }
}

public static class AnalysisTests
{
    [Test] public static void CommentFromObject() => Assert.Equal("x", FailureAnalyzer.CommentOf(JsonNode.Parse("""{"comment":"x"}"""), "comment,summary,output,text"));
    [Test] public static void CommentFromN8nArray() => Assert.Equal("y", FailureAnalyzer.CommentOf(JsonNode.Parse("""[{"output":"y"}]"""), "comment,summary,output,text"));
    [Test] public static void CommentFromPlainString() => Assert.Equal("z", FailureAnalyzer.CommentOf(JsonValue.Create("z"), "comment"));
    [Test] public static void EmptyCommentIsNone() => Assert.Null(FailureAnalyzer.CommentOf(JsonNode.Parse("""{"comment":"  ","other":1}"""), "comment"));

    [Test]
    public static void CallbackIsSingleUseAndNeedsTheToken()
    {
        var hub = new AnalysisCallbacks();
        var (id, token, answer) = hub.Register();
        Assert.False(hub.Complete(id, "wrong", null), "wrong token");
        Assert.False(hub.Complete("unknown", token, null), "unknown id");
        Assert.True(hub.Complete(id, token, JsonNode.Parse("""{"comment":"ok"}""")), "right token");
        Assert.True(answer.IsCompleted, "answer delivered");
        Assert.False(hub.Complete(id, token, null), "second use");
    }

    [Test]
    public static void CommentClaimWinsOnce()
    {
        var r = new ExecutionResult();
        Assert.True(r.ClaimCommentPost(), "first");
        Assert.False(r.ClaimCommentPost(), "second");
    }
}

public static class DiffTests
{
    [Test]
    public static void FindsChangedLinesOnly()
    {
        var lines = JsonDiff.Lines("""{"a":1,"b":2}""", """{"a":1,"b":3}""");
        Assert.Equal(1, lines.Count(l => l.Kind == DiffKind.Removed));
        Assert.Equal(1, lines.Count(l => l.Kind == DiffKind.Added));
        Assert.Contains("\"b\": 3", lines.Single(l => l.Kind == DiffKind.Added).Text);
    }

    [Test] public static void NewFail() => Assert.Equal("NEW FAIL", JsonDiff.Trend("PASSED", "FAILED"));
    [Test] public static void Fixed() => Assert.Equal("FIXED", JsonDiff.Trend("TIMEOUT", "PASSED"));
    [Test] public static void NoTrendWhenUnchanged() => Assert.Null(JsonDiff.Trend("FAILED", "FAILED"));
    [Test] public static void NoTrendWithoutHistory() => Assert.Null(JsonDiff.Trend(null, "FAILED"));

    [Test]
    public static void RerunGoesBackToTheTestCase()
    {
        var stored = new StoredResult
        {
            RunId = "r", Position = 1, ItemId = "90001", TestCaseId = "10232", Testablauf = "TA 1",
            RequestJson = """{"itemId":"90001","name":"TC","timeLimitSec":60,"releases":[{"name":"R5"}]}"""
        };
        var p = Assert.NotNull(Rerun.PayloadOf(stored));
        Assert.Equal("10232", p.ItemId, "itemId = test case");
        Assert.Equal("10232", p.SourceTestCaseId);
        Assert.Equal("TA 1", p.GroupName);
        Assert.True(p.Extra?["releases"] is JsonArray, "extras kept");
    }
}

public static class ApiClientTests
{
    private static ApiClientRegistry Registry(ApiOptions api, string legacy = "") =>
        new(new Monitor<ApiOptions>(api), new Monitor<CiOptions>(new CiOptions { ApiKey = legacy }));

    [Test]
    public static void PlainKeyWithScopes()
    {
        var r = Registry(new ApiOptions { Clients = { new() { Name = "n8n", Key = "k1", Scopes = new[] { "history:read" } } } });
        var c = Assert.NotNull(r.Find("k1"));
        Assert.Equal("n8n", c.Name);
        Assert.True(c.Can(Scopes.HistoryRead));
        Assert.False(c.Can(Scopes.RunsStart));
        Assert.Null(r.Find("k2"), "unknown key");
        Assert.Null(r.Find(""), "empty key");
    }

    [Test]
    public static void HashedKey()
    {
        // sha256("secret") 
        var r = Registry(new ApiOptions { Clients = { new() { Name = "h", KeySha256 = "2BB80D537B1DA3E38BD30361AA855686BDE0EACD7162FEF6A25FE97BF527A25B", Scopes = new[] { "audit:read" } } } });
        Assert.Equal("h", r.Find("secret")?.Name);
    }

    [Test]
    public static void DisabledClientIsRejected() =>
        Assert.Null(Registry(new ApiOptions { Clients = { new() { Name = "x", Key = "k", Disabled = true } } }).Find("k"));

    [Test]
    public static void LegacyCiKeyStillWorks()
    {
        var c = Assert.NotNull(Registry(new ApiOptions(), legacy: "old").Find("old"));
        Assert.Equal("ci", c.Name);
        Assert.True(c.Can(Scopes.RunsStart) && c.Can(Scopes.RunsRead));
        Assert.False(c.Can(Scopes.HistoryRead));
    }

    [Test]
    public static void ConfigProblemsAreReported()
    {
        var problems = Registry(new ApiOptions { Clients = { new() { Name = "a", Scopes = new[] { "history:raed" } }, new() { Name = "A", Key = "x" } } }).Problems().ToList();
        Assert.True(problems.Any(p => p.Contains("neither Key")), "missing key");
        Assert.True(problems.Any(p => p.Contains("history:raed")), "typo in scope");
        Assert.True(problems.Any(p => p.Contains("used 2 times")), "duplicate name");
    }

    [Test]
    public static void ServiceAccountCredentials()
    {
        var r = Registry(new ApiOptions { Clients = { new() { Name = "bot", Key = "k", CodebeamerToken = "t" }, new() { Name = "plain", Key = "p" } } });
        Assert.True(r.Find("k")!.Codebeamer is { IsToken: true }, "token account");
        Assert.Null(r.Find("p")!.Codebeamer, "no account");
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

public static class EventAndReportTests
{
    // HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog") — RFC 4231-style known value
    [Test]
    public static void SignatureMatchesKnownValue() =>
        Assert.Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            WebhookEventPublisher.Sign("key", "The quick brown fox jumps over the lazy dog"));

    [Test]
    public static void JUnitShowsFailureAndAiComment()
    {
        var job = new CiJob { Kind = "testRun", Target = "88132", FrameworkUrl = "http://bench", RequestedBy = "jdoe" };
        job.Results.Add(new ExecutionResult { ItemId = "88132", Name = "BT pairing", Verdict = Verdict.Failed, Message = "step 2", AnalysisComment = "check pairing list" });
        job.Results.Add(new ExecutionResult { ItemId = "88131", Name = "CAN wake", Verdict = Verdict.Passed });
        var xml = JUnitReport.Write(job);
        Assert.Contains("failures=\"1\"", xml);
        Assert.Contains("AI: check pairing list", xml);
        Assert.Contains("tests=\"2\"", xml);
    }
}
