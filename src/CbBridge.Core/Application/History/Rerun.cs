using System.Text.Json;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.History;

public static class Rerun
{
    /// <summary>
    /// The exact request a stored result was run with, ready to run again. A Run multiple result goes back to its
    /// test case (a new test run is created again if that option is on), not to the run created last time.
    /// </summary>
    public static TestRunPayload? PayloadOf(StoredResult r)
    {
        if (string.IsNullOrWhiteSpace(r.RequestJson)) return null;
        try
        {
            var p = TestRunPayload.FromJson(r.RequestJson);
            p.IsLocal = r.IsLocal;
            p.GroupName = r.Testablauf;
            if (r.TestCaseId is not null)
            {
                p.SourceTestCaseId = r.TestCaseId;
                p.ItemId = r.TestCaseId;
            }
            return p;
        }
        catch (JsonException) { return null; }
    }
}

public enum DiffKind { Same, Removed, Added }
public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>Line diff of two JSON documents (pretty-printed first, so key order and spacing don't matter much).</summary>
public static class JsonDiff
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string PrettyOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try { return System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(Pretty) ?? ""; }
        catch (JsonException) { return json; }
    }

    /// <summary>Longest-common-subsequence diff. Very long documents are compared line by line instead.</summary>
    public static List<DiffLine> Lines(string? before, string? after)
    {
        var a = PrettyOf(before).Split('\n');
        var b = PrettyOf(after).Split('\n');
        if ((long)a.Length * b.Length > 4_000_000) return Naive(a, b);

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add(new(DiffKind.Same, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add(new(DiffKind.Removed, a[x++]));
            else result.Add(new(DiffKind.Added, b[y++]));
        }
        while (x < a.Length) result.Add(new(DiffKind.Removed, a[x++]));
        while (y < b.Length) result.Add(new(DiffKind.Added, b[y++]));
        return result;
    }

    private static List<DiffLine> Naive(string[] a, string[] b)
    {
        var result = new List<DiffLine>();
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i < a.Length && i < b.Length && a[i] == b[i]) { result.Add(new(DiffKind.Same, a[i])); continue; }
            if (i < a.Length) result.Add(new(DiffKind.Removed, a[i]));
            if (i < b.Length) result.Add(new(DiffKind.Added, b[i]));
        }
        return result;
    }

    /// <summary>"NEW FAIL" when it passed last time and fails now, "FIXED" for the opposite.</summary>
    public static string? Trend(string? previousVerdict, string currentVerdict)
    {
        if (previousVerdict is null) return null;
        var wasOk = previousVerdict == "PASSED";
        var isOk = currentVerdict == "PASSED";
        var isProblem = currentVerdict is "FAILED" or "TIMEOUT" or "ERROR" or "BLOCKED";
        if (wasOk && isProblem) return "NEW FAIL";
        if (!wasOk && previousVerdict is "FAILED" or "TIMEOUT" or "ERROR" or "BLOCKED" && isOk) return "FIXED";
        return null;
    }
}
