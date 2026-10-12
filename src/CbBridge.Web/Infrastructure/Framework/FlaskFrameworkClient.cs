using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CbBridge.Web.Application;
using CbBridge.Web.Application.Abstractions;
using CbBridge.Web.Application.Execution;
using CbBridge.Web.Domain;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Infrastructure.Framework;

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
            return code switch
            {
                >= 200 and < 300 => (true, $"Reachable · {options.Value.HealthPath} → {code}"),
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
