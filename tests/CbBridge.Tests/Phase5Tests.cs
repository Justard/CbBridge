using CbBridge.Core.Application;
using CbBridge.Core.Application.Editing;
using CbBridge.Core.Application.Execution;
using CbBridge.Core.Domain;

namespace CbBridge.Tests;

public static class BenchAndAccessTests
{
    [Test]
    public static void BenchIsBusyUntilTheRunEnds()
    {
        var r = new BenchRegistry();
        var changes = 0;
        r.Changed += () => changes++;
        r.Begin("http://bench-a:5001/", "jdoe", "console", "#1, #2", 2, "run-1");
        Assert.Equal("jdoe", r.BusyWith("http://bench-a:5001")?.Who, "same bench with/without slash");
        r.Progress("http://bench-a:5001", 1, "second test");
        Assert.Equal("second test", r.BusyWith("http://bench-a:5001")?.Current);
        r.End("http://bench-a:5001");
        Assert.Null(r.BusyWith("http://bench-a:5001"), "free again");
        Assert.Equal("jdoe", r.Recent.Single().Who, "last use kept");
        Assert.Equal(3, changes, "listeners told");
    }

    [Test]
    public static void PostBackAccess()
    {
        Assert.True(new AccessOptions().MayPostBack("anyone"), "empty list = everyone");
        var limited = new AccessOptions { PostBackUsers = new[] { "ci-agent", "JDoe" } };
        Assert.True(limited.MayPostBack("jdoe"), "case-insensitive");
        Assert.False(limited.MayPostBack("asmith"));
    }

    [Test]
    public static void PresetKeepsTestCaseAndFlags()
    {
        var p = new TestRunPayload { ItemId = "10232", Name = "TC", SourceTestCaseId = "10232", GroupName = "TA", Analyze = false, TimeLimitSec = 30 };
        var back = Assert.NotNull(PresetTest.From(p, include: false).ToPayload());
        Assert.Equal("10232", back.SourceTestCaseId);
        Assert.Equal("TA", back.GroupName);
        Assert.False(back.Analyze, "analyze flag");
        Assert.Equal(30, back.TimeLimitSec);
    }

    [Test]
    public static void MetricsArePrometheusText()
    {
        var m = new BridgeMetrics();
        m.RunStarted();
        m.Count("cbbridge_tests_total", ("verdict", "FAILED"), ("source", "ci"));
        m.Count("cbbridge_tests_total", ("verdict", "FAILED"), ("source", "ci"));
        m.Count("cbbridge_events_total", ("event", "x\"y"), ("outcome", "failed"));
        var text = m.Prometheus(activeBenches: 1);
        Assert.Contains("cbbridge_runs_active 1", text);
        Assert.Contains("cbbridge_benches_busy 1", text);
        Assert.Contains("cbbridge_tests_total{verdict=\"FAILED\",source=\"ci\"} 2", text);
        Assert.Contains("event=\"x\\\"y\"", text, "label quotes escaped");
        Assert.Contains("# TYPE cbbridge_tests_total counter", text);
    }
}
