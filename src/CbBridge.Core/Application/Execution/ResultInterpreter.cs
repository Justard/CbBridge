using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application.Json;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Execution;

/// <summary>Reads the framework's result JSON into a verdict. Unknown shapes become Error, never a crash.</summary>
public static class ResultInterpreter
{
    public static ExecutionResult Interpret(TestRunPayload sent, int httpStatus, string body, TimeSpan duration, FrameworkOptions options)
    {
        var result = new ExecutionResult
        {
            ItemId = sent.ItemId,
            Name = sent.Name,
            Duration = duration,
            RawJson = body
        };

        JsonNode? json;
        try { json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body); }
        catch (JsonException)
        {
            result.Verdict = Verdict.Error;
            result.Message = $"HTTP {httpStatus}, response is not JSON";
            return result;
        }

        var verdictText = JsonPathLite.Display(JsonPathLite.Get(json, options.VerdictFields), 200);
        result.Message = JsonPathLite.Display(JsonPathLite.Get(json, options.MessageFields), 300);

        if (JsonPathLite.Get(json, "durationSec") is JsonValue d && d.TryGetValue<double>(out var secs))
            result.Duration = TimeSpan.FromSeconds(secs);

        result.Verdict = Map(verdictText);
        if (result.Verdict == Verdict.Error && httpStatus is >= 200 and < 300 && string.IsNullOrEmpty(result.Message))
            result.Message = string.IsNullOrEmpty(verdictText) ? "no verdict field in result JSON" : $"unknown verdict '{verdictText}'";
        if (httpStatus is < 200 or >= 300 && string.IsNullOrEmpty(result.Message))
            result.Message = $"HTTP {httpStatus}";
        return result;
    }

    public static Verdict Map(string text) => text.Trim().ToUpperInvariant() switch
    {
        "PASSED" or "PASS" or "OK" or "SUCCESS" => Verdict.Passed,
        "FAILED" or "FAIL" or "FAILURE" => Verdict.Failed,
        "BLOCKED" => Verdict.Blocked,
        "TIMEOUT" or "TIMED_OUT" or "TIMEDOUT" => Verdict.Timeout,
        "ABORTED" or "CANCELLED" or "CANCELED" => Verdict.Aborted,
        _ => Verdict.Error
    };
}
