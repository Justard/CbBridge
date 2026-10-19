using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application;
using CbBridge.Core.Application.Analysis;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Ci;

/// <summary>
/// Where the AI agent sends its answer when it works asynchronously:
///   POST /api/analysis/callback/{analysisId}   header X-Callback-Token: {token from the request}
///   body: {"comment": "…", …anything else the agent wants to keep}
/// Each ID/token pair is single-use and only valid while the bridge waits for it. Off unless Analysis:Enabled.
/// </summary>
public static class AnalysisEndpoints
{
    public static void MapAnalysisCallback(this WebApplication app)
    {
        app.MapPost("/api/analysis/callback/{id}", async (string id, HttpContext http, AnalysisCallbacks callbacks,
            IOptions<AnalysisOptions> options) =>
        {
            if (!options.Value.Enabled)
                return Results.Problem(statusCode: 404, detail: "AI analysis is disabled (Analysis:Enabled = false).");

            JsonNode? body;
            try { body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted); }
            catch (JsonException) { return Results.Problem(statusCode: 400, detail: "Body must be JSON, e.g. {\"comment\": \"…\"}."); }

            var token = http.Request.Headers["X-Callback-Token"].FirstOrDefault()
                        ?? (body as JsonObject)?["token"]?.GetValue<string>();
            if (body is JsonObject obj) obj.Remove("token");   // never keep the token in result files

            return callbacks.Complete(id, token, body)
                ? Results.Json(new { accepted = true })
                : Results.Problem(statusCode: 404, detail: "Unknown or expired analysis ID, or wrong token.");
        });
    }
}
