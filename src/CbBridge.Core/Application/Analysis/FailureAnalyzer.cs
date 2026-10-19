using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Json;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Analysis;

/// <summary>Outcome of one analysis.</summary>
public sealed record AnalysisOutcome(AnalysisState State, string? Comment, string? RawJson, string Message);

/// <summary>
/// Builds the request for the AI agent (contract "cbbridge.analysis/v1", see docs/ai-analysis.md), sends it and
/// waits for the answer — directly from the webhook response or via the callback. Never throws.
/// </summary>
public sealed class FailureAnalyzer(IAnalysisAgent agent, AnalysisCallbacks callbacks, IOptions<AnalysisOptions> options,
    IOptions<Api.BridgeOptions> bridge)
{
    public const string Contract = "cbbridge.analysis/v1";

    public bool Enabled => options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.WebhookUrl);

    /// <summary>Run switch on, test's checkbox on, and a verdict that is worth analysing.</summary>
    public bool Wants(RunOptions run, ExecutionResult result) =>
        Enabled && run.Analyze && result.Analyze &&
        options.Value.EffectiveVerdicts.Contains(result.Verdict.ToString(), StringComparer.OrdinalIgnoreCase);

    public async Task<AnalysisOutcome> AnalyzeAsync(TestRunPayload sent, ExecutionResult result, Results.RunRecord? run,
        string? codebeamerBaseUrl, CancellationToken ct = default)
    {
        var o = options.Value;
        var callbackBase = !string.IsNullOrWhiteSpace(o.CallbackBaseUrl) ? o.CallbackBaseUrl : bridge.Value.PublicUrl;
        var useCallback = !string.IsNullOrWhiteSpace(callbackBase);
        var (id, token, answerTask) = useCallback ? callbacks.Register() : (Guid.NewGuid().ToString("N")[..24], "", Task.FromResult<JsonNode?>(null));
        try
        {
            var request = BuildRequest(id, useCallback ? (CallbackUrl(callbackBase, id), token) : null, sent, result, run, codebeamerBaseUrl, o);
            var reply = await agent.RequestAsync(request, ct);
            if (reply.Error is not null)
                return new(AnalysisState.Failed, null, null, reply.Error);

            if (!reply.Accepted)
                return FromAnswer(reply.Answer, "answered directly");

            if (!useCallback)
                return new(AnalysisState.NoAnswer, null, null,
                    "the agent accepted the job but neither Analysis:CallbackBaseUrl nor Bridge:PublicUrl is set, so it cannot send the answer back");

            var wait = TimeSpan.FromSeconds(Math.Max(5, o.CallbackWaitSec));
            var finished = await Task.WhenAny(answerTask, Task.Delay(wait, ct));
            return finished == answerTask
                ? FromAnswer(await answerTask, "answered via callback")
                : new(AnalysisState.NoAnswer, null, null, $"no callback within {wait.TotalSeconds:0}s");
        }
        catch (OperationCanceledException)
        {
            return new(AnalysisState.NoAnswer, null, null, "stopped waiting");
        }
        catch (Exception ex)
        {
            return new(AnalysisState.Failed, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (useCallback) callbacks.Forget(id);
        }
    }

    private AnalysisOutcome FromAnswer(JsonNode? answer, string how)
    {
        var comment = CommentOf(answer, options.Value.CommentFields);
        var raw = answer?.ToJsonString();
        return string.IsNullOrWhiteSpace(comment)
            ? new(AnalysisState.NoAnswer, null, raw, $"{how}, but without a comment ({options.Value.CommentFields})")
            : new(AnalysisState.Done, comment.Trim(), raw, how);
    }

    /// <summary>First non-empty text among the comment fields; n8n often wraps the answer in a one-element array.</summary>
    public static string? CommentOf(JsonNode? answer, string fields)
    {
        if (answer is JsonArray { Count: > 0 } a) answer = a[0];
        if (answer is JsonValue v && v.TryGetValue<string>(out var plain)) return plain;
        if (answer is not JsonObject obj) return null;
        foreach (var f in fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (obj[f] is JsonValue fv && fv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        return null;
    }

    private static string CallbackUrl(string baseUrl, string id) =>
        $"{baseUrl.TrimEnd('/')}/api/analysis/callback/{id}";

    /// <summary>The "cbbridge.analysis/v1" request. Only data the bridge already has — no credentials.</summary>
    private static JsonObject BuildRequest(string id, (string Url, string Token)? callback, TestRunPayload sent,
        ExecutionResult r, Results.RunRecord? run, string? cbBase, AnalysisOptions o)
    {
        JsonNode? framework = null;
        if (!string.IsNullOrWhiteSpace(r.RawJson))
        {
            if (r.RawJson.Length > o.MaxFrameworkChars)
                framework = JsonValue.Create(r.RawJson[..o.MaxFrameworkChars] + "… (cut)");
            else
                try { framework = JsonNode.Parse(r.RawJson); }
                catch (JsonException) { framework = JsonValue.Create(r.RawJson); }
        }

        var cbTarget = r.PostTargetId is { } t && !t.StartsWith("dryrun-") ? t : null;
        return new JsonObject
        {
            ["contract"] = Contract,
            ["analysisId"] = id,
            ["callback"] = callback is null ? null : new JsonObject
            {
                ["url"] = callback.Value.Url,
                ["token"] = callback.Value.Token,
                ["header"] = "X-Callback-Token"
            },
            ["hints"] = new JsonObject { ["language"] = o.Language, ["maxWords"] = o.MaxWords },
            ["run"] = run is null ? null : new JsonObject
            {
                ["id"] = run.Id,
                ["source"] = run.Source,
                ["label"] = run.Label,
                ["selection"] = run.Selection,
                ["frameworkUrl"] = run.FrameworkUrl
            },
            ["test"] = new JsonObject
            {
                ["itemId"] = sent.ItemId,
                ["testCaseId"] = r.TestCaseId,
                ["testablauf"] = r.GroupName,
                ["name"] = sent.Name,
                ["description"] = sent.Description,
                ["testConfiguration"] = sent.TestConfiguration,
                ["timeLimitSec"] = sent.TimeLimitSec,
                ["steps"] = sent.TestRunSteps?.DeepClone(),
                ["parameters"] = sent.Parameters?.DeepClone()
            },
            ["result"] = new JsonObject
            {
                ["verdict"] = r.Verdict.ToString().ToUpperInvariant(),
                ["durationSec"] = Math.Round(r.Duration.TotalSeconds, 2),
                ["message"] = r.Message,
                ["framework"] = framework
            },
            ["codebeamer"] = new JsonObject
            {
                ["baseUrl"] = string.IsNullOrWhiteSpace(cbBase) ? null : cbBase,
                ["testRunId"] = cbTarget,
                ["itemUrl"] = cbTarget is null || string.IsNullOrWhiteSpace(cbBase) ? null : $"{cbBase.TrimEnd('/')}/issue/{cbTarget}"
            }
        };
    }
}
