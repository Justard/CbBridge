using System.Globalization;
using System.Xml.Linq;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Ci;

/// <summary>
/// JUnit XML for Jenkins' "junit" step: one &lt;testcase&gt; per executed test, grouped by Testablauf.
/// FAILED/TIMEOUT/BLOCKED → failure, ERROR → error, NOT RUN/ABORTED → skipped.
/// </summary>
public static class JUnitReport
{
    public static string Write(CiJob job)
    {
        var results = job.Results.ToList();
        var suite = new XElement("testsuite",
            new XAttribute("name", $"codebeamer {job.Kind} {job.Target}"),
            new XAttribute("tests", results.Count),
            new XAttribute("failures", results.Count(r => r.Verdict is Verdict.Failed or Verdict.Timeout or Verdict.Blocked)),
            new XAttribute("errors", results.Count(r => r.Verdict == Verdict.Error)),
            new XAttribute("skipped", results.Count(r => r.Verdict is Verdict.NotRun or Verdict.Aborted or Verdict.Queued)),
            new XAttribute("time", Seconds(results.Sum(r => r.Duration.TotalSeconds))),
            new XAttribute("timestamp", (job.StartedAt ?? job.CreatedAt).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));

        foreach (var r in results)
        {
            var testcase = new XElement("testcase",
                new XAttribute("classname", ClassName(job, r)),
                new XAttribute("name", $"#{r.ItemId} {r.Name}"),
                new XAttribute("time", Seconds(r.Duration.TotalSeconds)));

            switch (r.Verdict)
            {
                case Verdict.Failed:
                case Verdict.Timeout:
                case Verdict.Blocked:
                    testcase.Add(new XElement("failure", new XAttribute("type", r.Verdict.ToString().ToUpperInvariant()),
                        new XAttribute("message", r.AnalysisComment is null ? r.Message : $"{r.Message} · AI: {r.AnalysisComment}"),
                        r.RawJson ?? r.Message));
                    break;
                case Verdict.Error:
                    testcase.Add(new XElement("error",
                        new XAttribute("message", r.AnalysisComment is null ? r.Message : $"{r.Message} · AI: {r.AnalysisComment}"),
                        r.RawJson ?? r.Message));
                    break;
                case Verdict.NotRun:
                case Verdict.Aborted:
                case Verdict.Queued:
                    testcase.Add(new XElement("skipped", new XAttribute("message", string.IsNullOrEmpty(r.Message) ? r.Verdict.ToString() : r.Message)));
                    break;
            }

            var info = new List<string>();
            if (r.TestCaseId is not null) info.Add($"test case #{r.TestCaseId}");
            if (r.PostTargetId is not null) info.Add($"codebeamer test run #{r.PostTargetId}");
            if (r.PostMessage is not null) info.Add($"upload: {r.PostMessage}");
            if (r.AnalysisComment is not null) info.Add($"AI analysis: {r.AnalysisComment}");
            else if (r.AnalysisState is not (AnalysisState.None or AnalysisState.Done)) info.Add($"AI analysis: {r.AnalysisState}");
            if (info.Count > 0) testcase.Add(new XElement("system-out", string.Join("\n", info)));
            suite.Add(testcase);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("testsuites", suite)).ToString();
    }

    /// <summary>Jenkins shows classname as package.class: use the Testablauf when there is one.</summary>
    private static string ClassName(CiJob job, ExecutionResult r) =>
        (r.GroupName ?? $"codebeamer.{job.Kind}.{job.Target}").Replace(' ', '_');

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
