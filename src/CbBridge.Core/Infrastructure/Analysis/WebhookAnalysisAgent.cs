using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application;
using CbBridge.Core.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Infrastructure.Analysis;

/// <summary>
/// Any agent platform that accepts a webhook: n8n, Zapier, Make, Power Automate, or your own service.
/// 200 with a comment = answered now; 202 (or 200 without a comment) = will answer through the callback.
/// </summary>
public sealed class WebhookAnalysisAgent(IHttpClientFactory httpFactory, IOptions<AnalysisOptions> options) : IAnalysisAgent
{
    public async Task<AgentReply> RequestAsync(JsonObject request, CancellationToken ct = default)
    {
        var o = options.Value;
        using var message = new HttpRequestMessage(HttpMethod.Post, o.WebhookUrl)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(o.AuthHeader) && !string.IsNullOrWhiteSpace(o.AuthValue))
            message.Headers.TryAddWithoutValidation(o.AuthHeader, o.AuthValue);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.RequestTimeoutSec)));
        try
        {
            using var response = await httpFactory.CreateClient("analysis").SendAsync(message, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(null, false, $"agent answered HTTP {(int)response.StatusCode}: {Shorten(text)}");
            if (response.StatusCode == HttpStatusCode.Accepted)
                return new(null, true, null);

            JsonNode? answer;
            try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
            catch (JsonException) { answer = JsonValue.Create(text); }   // plain-text answer is a comment too

            var hasComment = Application.Analysis.FailureAnalyzer.CommentOf(answer, o.CommentFields) is { Length: > 0 };
            return new(answer, !hasComment, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, false, $"agent did not answer within {o.RequestTimeoutSec}s");
        }
        catch (HttpRequestException ex)
        {
            return new(null, false, "agent not reachable: " + ex.Message);
        }
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
