using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Editing;
using CbBridge.Core.Application.Json;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Infrastructure.Codebeamer;

/// <summary>All writing to codebeamer, through Swagger REST v3: result uploads and new test runs.</summary>
public sealed class SwaggerResultWriter(
    IHttpClientFactory httpFactory,
    CodebeamerSession session,
    IOptions<CodebeamerOptions> cbOptions,
    IOptions<PostBackOptions> postOptions,
    IOptions<RunMultipleOptions> runMultipleOptions) : ICodebeamerResultWriter
{
    public async Task<(PostState State, string Message)> PostResultAsync(ExecutionResult result, CancellationToken ct = default)
    {
        if (result.PostTargetId is null)
            return (PostState.Failed, "no codebeamer test run to post to");

        var path = $"{cbOptions.Value.V3Prefix}/testruns/{result.PostTargetId}";
        var body = BuildResultBody(result).ToJsonString();

        if (postOptions.Value.DryRun)
            return (PostState.DryRun, $"PUT {path} (dry run, not sent) {Shorten(body)}");

        var (ok, status, text) = await SendAsync(HttpMethod.Put, path, body, ct);
        return ok
            ? (PostState.Posted, $"PUT {path} → {status}")
            : (PostState.Failed, $"PUT {path} → {(status == 0 ? text : $"HTTP {status}: {Shorten(text)}")}");
    }

    public async Task<(string? NewRunId, PostState State, string Message)> CreateTestRunAsync(TestRunPayload testCase, CancellationToken ct = default)
    {
        var rm = runMultipleOptions.Value;
        if (string.IsNullOrWhiteSpace(rm.TestRunTrackerId))
            return (null, PostState.Failed, "RunMultiple:TestRunTrackerId is not set — cannot create test runs");

        var path = cbOptions.Value.V3Prefix + rm.CreatePath.Replace("{trackerId}", Uri.EscapeDataString(rm.TestRunTrackerId));
        var body = BuildCreateBody(testCase, rm).ToJsonString();

        if (postOptions.Value.DryRun)
            return ($"dryrun-{testCase.SourceTestCaseId}", PostState.DryRun, $"POST {path} (dry run, not sent) {Shorten(body)}");

        var (ok, status, text) = await SendAsync(HttpMethod.Post, path, body, ct);
        if (!ok)
            return (null, PostState.Failed, $"POST {path} → {(status == 0 ? text : $"HTTP {status}: {Shorten(text)}")}");

        var newId = ReadNewId(text);
        return newId is null
            ? (null, PostState.Failed, $"POST {path} → {status}, but no new test run ID in the answer: {Shorten(text)}")
            : (newId, PostState.Posted, $"POST {path} → {status} · new test run #{newId}");
    }

    /// <summary>
    /// ALIGN WITH YOUR INSTANCE: request body of PUT /api/v3/testruns/{testRunId} (see {your-cb}/cb/v3/swagger).
    /// </summary>
    private static JsonObject BuildResultBody(ExecutionResult r)
    {
        var body = new JsonObject
        {
            ["result"] = ToCodebeamerResult(r.Verdict),
            ["runTimeSeconds"] = Math.Round(r.Duration.TotalSeconds, 1),
            ["conclusion"] = r.Verdict == Verdict.Timeout ? $"Timed out. {r.Message}".Trim() : r.Message
        };
        if (!string.IsNullOrWhiteSpace(r.RawJson))
        {
            try { body["frameworkResult"] = JsonNode.Parse(r.RawJson); }
            catch (JsonException) { /* keep upload going without the raw result */ }
        }
        return body;
    }

    /// <summary>
    /// ALIGN WITH YOUR INSTANCE: request body of the "create test run" call (RunMultiple:CreatePath).
    /// One new test run per test case, with the release/versions chosen in Run multiple.
    /// </summary>
    private static JsonObject BuildCreateBody(TestRunPayload tc, RunMultipleOptions rm)
    {
        var name = rm.NewRunNameTemplate
            .Replace("{name}", tc.Name)
            .Replace("{testConfiguration}", tc.TestConfiguration ?? "")
            .Replace("{testablauf}", tc.GroupName ?? "")
            .Trim().TrimEnd('·', '-', '/', ' ');

        var body = new JsonObject
        {
            ["name"] = name,
            ["testCaseIds"] = new JsonArray(new JsonObject { ["id"] = ToNumberOrString(tc.SourceTestCaseId) }),
            ["testConfiguration"] = tc.TestConfiguration
        };
        foreach (var key in new[] { "releases", "versions" })
            if (tc.Extra?[key] is JsonNode value) body[key] = value.DeepClone();
        return body;
    }

    /// <summary>New ID from {"id":..}, {"uri":"/item/.."}, or the first element of a list.</summary>
    private static string? ReadNewId(string text)
    {
        try
        {
            var json = JsonNode.Parse(text);
            if (json is JsonArray a) json = a.FirstOrDefault();
            return JsonPathLite.IdOf(json);
        }
        catch (JsonException) { return null; }
    }

    private async Task<(bool Ok, int Status, string Text)> SendAsync(HttpMethod method, string path, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, session.BaseUrl + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        session.Authorize(request);
        try
        {
            using var response = await httpFactory.CreateClient("codebeamer").SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, text);
        }
        catch (HttpRequestException ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private static JsonNode? ToNumberOrString(string? id) =>
        long.TryParse(id, out var n) ? JsonValue.Create(n) : JsonValue.Create(id);

    private static string ToCodebeamerResult(Verdict v) => v switch
    {
        Verdict.Passed => "PASSED",
        Verdict.Blocked => "BLOCKED",
        _ => "FAILED" // Failed and Timeout
    };

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
