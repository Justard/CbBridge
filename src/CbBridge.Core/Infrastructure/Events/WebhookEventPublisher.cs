using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Infrastructure.Events;

/// <summary>
/// Sends events to the configured webhooks in the background (queue → one sender), 3 attempts each.
/// JSON bodies are signed with HMAC-SHA256 when the subscriber has a Secret.
/// </summary>
public sealed class WebhookEventPublisher(
    IHttpClientFactory httpFactory, IOptionsMonitor<EventOptions> options, Application.BridgeMetrics metrics, ILogger<WebhookEventPublisher> logger)
    : BackgroundService, IEventPublisher
{
    private readonly Channel<BridgeEvent> _queue = Channel.CreateBounded<BridgeEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Publish(BridgeEvent e)
    {
        if (options.CurrentValue.Subscribers.Any(s => Wants(s, e)))
            _queue.Writer.TryWrite(e);
    }

    private static bool Wants(EventSubscriber s, BridgeEvent e) =>
        !s.Disabled && !string.IsNullOrWhiteSpace(s.Url)
        && (s.Events.Length == 0 || s.Events.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
        && (!s.OnlyProblems || e.IsProblem);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var e in _queue.Reader.ReadAllAsync(stop))
            foreach (var s in options.CurrentValue.Subscribers.Where(s => Wants(s, e)).ToList())
                await SendAsync(s, e, stop);
    }

    private async Task SendAsync(EventSubscriber s, BridgeEvent e, CancellationToken stop)
    {
        var body = s.Format.Equals("text", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["text"] = e.Text }.ToJsonString()
            : e.Data.ToJsonString();
        var delays = new[] { 0, 2, 10 };
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            if (delays[attempt] > 0) await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), stop);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, s.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("X-Bridge-Event", e.Name);
                if (!string.IsNullOrEmpty(s.Secret))
                    request.Headers.TryAddWithoutValidation("X-Bridge-Signature", "sha256=" + Sign(s.Secret, body));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await httpFactory.CreateClient("events").SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode) { metrics.Count("cbbridge_events_total", ("event", e.Name), ("outcome", "delivered")); return; }
                logger.LogWarning("Event {Event} → {Subscriber}: HTTP {Status}", e.Name, s.Name, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !stop.IsCancellationRequested)
            {
                logger.LogWarning("Event {Event} → {Subscriber}: {Error}", e.Name, s.Name, ex.Message);
            }
        }
        metrics.Count("cbbridge_events_total", ("event", e.Name), ("outcome", "failed"));
    }

    /// <summary>Hex HMAC-SHA256 of the exact body — receivers compute the same and compare.</summary>
    public static string Sign(string secret, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
