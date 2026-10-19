using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CbBridge.Core.Application;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Execution;
using CbBridge.Core.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Infrastructure.Framework;

/// <summary>
/// Talks to your Python framework over Flask REST.
/// The framework enforces timeLimitSec itself; the HTTP wait (limit + grace) is only a safety net,
/// and when it fires the bridge calls /cancel so the test really stops.
/// </summary>
public sealed class FlaskFrameworkClient(
    IHttpClientFactory httpFactory,
    IOptions<FrameworkOptions> options,
    FrameworkTarget target,
    ILogger<FlaskFrameworkClient> logger) : IFrameworkClient
{
    public string BaseUrl => target.BaseUrl;

    public async Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default)
    {
        var url = BaseUrl + options.Value.HealthPath;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await httpFactory.CreateClient("framework").GetAsync(url, cts.Token);
            var code = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return code switch
            {
                >= 200 and < 300 => (true, $"Reachable · {options.Value.HealthPath} → {code}{ContractInfo(body)}"),
                404 => (true, $"Reachable · no {options.Value.HealthPath} route (404), but the framework answers"),
                _ => (false, $"Answered with HTTP {code}")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"No answer from {BaseUrl} within 5 s");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Not reachable: {ex.Message}");
        }
    }

    public async Task<ExecutionResult> ExecuteAsync(TestRunPayload payload, TimeSpan httpWait, CancellationToken abort)
    {
        var o = options.Value;
        using var timeout = new CancellationTokenSource(httpWait);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, abort);
        var sw = Stopwatch.StartNew();

        try
        {
            var json = payload.ToJson();
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpFactory.CreateClient("framework")
                .PostAsync(BaseUrl + o.ExecutePath, content, linked.Token);
            var body = await response.Content.ReadAsStringAsync(linked.Token);
            return ResultInterpreter.Interpret(payload, (int)response.StatusCode, body, sw.Elapsed, o);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            var cancelled = await CancelAsync(payload.ItemId);
            return Bridge(payload, Verdict.Aborted, sw.Elapsed, cancelled ? "aborted · /cancel sent" : "aborted · /cancel failed, check framework");
        }
        catch (OperationCanceledException)
        {
            var cancelled = await CancelAsync(payload.ItemId);
            return Bridge(payload, Verdict.Timeout, sw.Elapsed,
                $"framework did not answer within {httpWait.TotalSeconds:0}s · " + (cancelled ? "/cancel sent" : "/cancel failed, check framework"));
        }
        catch (HttpRequestException ex)
        {
            return Bridge(payload, Verdict.Error, sw.Elapsed, $"cannot reach framework at {BaseUrl}: {ex.Message}");
        }
    }

    /// <summary>" · contract 1.1 · progress, attachments · framework_stub 1.1" from a /health answer, if it says so.</summary>
    private static string ContractInfo(string body)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body) is not System.Text.Json.Nodes.JsonObject o) return "";
            var parts = new List<string>();
            if (o["contract"]?.ToString() is { } c) parts.Add("contract " + c);
            if (o["capabilities"] is System.Text.Json.Nodes.JsonArray caps) parts.Add(string.Join(", ", caps.Select(x => x?.ToString())));
            if (o["framework"]?.ToString() is { } f) parts.Add(f);
            return parts.Count == 0 ? " · contract 1.0" : " · " + string.Join(" · ", parts);
        }
        catch (JsonException) { return ""; }
    }

    // Set once the framework answers 404/405 on the progress route without a running-state body: stop asking.
    private string? _progressUnsupportedAt;   // framework address that has no progress route

    public async Task<FrameworkProgress?> ProgressAsync(string itemId, CancellationToken ct = default)
    {
        var o = options.Value;
        if (_progressUnsupportedAt == BaseUrl || string.IsNullOrWhiteSpace(o.ProgressPath)) return new FrameworkProgress(0, null, null, Supported: false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var url = BaseUrl + o.ProgressPath.Replace("{itemId}", Uri.EscapeDataString(itemId));
            using var response = await httpFactory.CreateClient("framework").GetAsync(url, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 404 with {"running": false} = supported, just not (yet/any more) running; anything else = no such route
                if (!text.Contains("\"running\"")) _progressUnsupportedAt = BaseUrl;
                return null;
            }
            if (System.Text.Json.Nodes.JsonNode.Parse(text) is not System.Text.Json.Nodes.JsonObject p) return null;
            var last = p["lastStep"] switch
            {
                System.Text.Json.Nodes.JsonObject step => $"{step["action"]} → {step["verdict"]}",
                { } other => other.ToString(),
                null => null
            };
            return new FrameworkProgress(
                p["stepsDone"] is { } d && int.TryParse(d.ToString(), out var done) ? done : 0,
                p["stepsTotal"] is { } t && int.TryParse(t.ToString(), out var total) ? total : null,
                last);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<bool> CancelAsync(string itemId)
    {
        var o = options.Value;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var url = BaseUrl + o.CancelPath.Replace("{itemId}", Uri.EscapeDataString(itemId));
            using var response = await httpFactory.CreateClient("framework").PostAsync(url, null, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Cancel for {ItemId} failed", itemId);
            return false;
        }
    }

    private static ExecutionResult Bridge(TestRunPayload p, Verdict v, TimeSpan d, string message) => new()
    {
        ItemId = p.ItemId,
        Name = p.Name,
        Verdict = v,
        Duration = d,
        Message = message
    };
}
