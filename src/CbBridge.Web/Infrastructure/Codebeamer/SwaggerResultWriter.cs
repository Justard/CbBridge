using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Web.Application;
using CbBridge.Web.Application.Abstractions;
using CbBridge.Web.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Infrastructure.Codebeamer;

/// <summary>Uploads results through codebeamer Swagger REST v3.</summary>
public sealed class SwaggerResultWriter(
    IHttpClientFactory httpFactory,
    CodebeamerSession session,
    IOptions<CodebeamerOptions> cbOptions,
    IOptions<PostBackOptions> postOptions) : ICodebeamerResultWriter
{
    public async Task<(PostState State, string Message)> PostResultAsync(ExecutionResult result, CancellationToken ct = default)
    {
        var path = $"{cbOptions.Value.V3Prefix}/testruns/{result.ItemId}";
        var body = BuildBody(result).ToJsonString();

        if (postOptions.Value.DryRun)
            return (PostState.DryRun, $"PUT {path} (dry run, not sent) {Shorten(body)}");

        using var request = new HttpRequestMessage(HttpMethod.Put, session.BaseUrl + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        session.Authorize(request);

        try
        {
            using var response = await httpFactory.CreateClient("codebeamer").SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? (PostState.Posted, $"PUT {path} → {(int)response.StatusCode}")
                : (PostState.Failed, $"PUT {path} → HTTP {(int)response.StatusCode}: {Shorten(text)}");
        }
        catch (HttpRequestException ex)
        {
            return (PostState.Failed, $"PUT {path} → {ex.Message}");
        }
    }

    /// <summary>
    /// ALIGN THIS WITH YOUR INSTANCE: open {your-cb}/cb/v3/swagger and check the request body of
    /// PUT /api/v3/testruns/{testRunId}. This is the only place that knows the upload format.
    /// </summary>
    private static JsonObject BuildBody(ExecutionResult r)
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

    private static string ToCodebeamerResult(Verdict v) => v switch
    {
        Verdict.Passed => "PASSED",
        Verdict.Blocked => "BLOCKED",
        _ => "FAILED" // Failed and Timeout
    };

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
