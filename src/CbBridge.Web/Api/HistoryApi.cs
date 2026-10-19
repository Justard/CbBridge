using System.Text.Json.Nodes;
using CbBridge.Core.Application.Abstractions;
using CbBridge.Core.Application;
using CbBridge.Core.Application.Api;
using CbBridge.Core.Application.History;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Api;

/// <summary>
/// /api/v1 — read access to the run history for agents, dashboards and scripts (OpenAPI: /api/openapi.json).
/// Key in X-Api-Key or Authorization: Bearer. Off unless Api:Enabled.
/// </summary>
public static class HistoryApi
{
    public static void MapHistoryApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1").AddEndpointFilter(ApiAuth.RequireApiEnabled);
        var read = ApiAuth.Require(Scopes.HistoryRead, allowBearer: true);

        v1.MapGet("/status", (IResultRepository repo, IOptions<AnalysisOptions> analysis, IOptions<CbBridge.Core.Application.Ci.CiOptions> ci,
            CbBridge.Core.Licensing.LicenceService licence, HttpContext http) => Results.Json(new
        {
            licence = new { state = licence.Check.State.ToString(), licence.Check.Message, licensee = licence.Check.Grant?.Licensee, expiresAt = licence.Check.Grant?.ExpiresAt, enforced = licence.Enforced, prerelease = licence.IsPrerelease },
            client = http.Client()!.Name,
            scopes = http.Client()!.Scopes.OrderBy(s => s),
            version = typeof(HistoryApi).Assembly.GetName().Version?.ToString(),
            history = new { available = repo.Available, status = repo.Status },
            ci = new { enabled = ci.Value.Enabled },
            aiAnalysis = new { enabled = analysis.Value.Enabled && !string.IsNullOrWhiteSpace(analysis.Value.WebhookUrl) }
        })).AddEndpointFilter(read);

        v1.MapGet("/runs", (IResultRepository repo, IOptions<BridgeOptions> bridge, HttpContext http,
            string? text, string? source, string? verdict, DateTimeOffset? from, DateTimeOffset? to, int? limit, int? offset) =>
        {
            if (!repo.Available) return NoHistory(repo);
            var (runs, total) = repo.ListRuns(new RunQuery(text, source, verdict, from, to, limit ?? 50, offset ?? 0));
            var b = ApiViews.BaseUrl(http, bridge.Value.PublicUrl);
            return Results.Json(new JsonObject
            {
                ["total"] = total, ["offset"] = offset ?? 0,
                ["runs"] = new JsonArray(runs.Select(r => (JsonNode?)ApiViews.Run(r, b)).ToArray())
            });
        }).AddEndpointFilter(read);

        v1.MapGet("/runs/{runId}", (string runId, IResultRepository repo, IOptions<BridgeOptions> bridge, HttpContext http) =>
        {
            if (!repo.Available) return NoHistory(repo);
            if (repo.GetRun(runId) is not { } run) return ApiAuth.Problem(404, $"No run {runId}.");
            var b = ApiViews.BaseUrl(http, bridge.Value.PublicUrl);
            var body = ApiViews.Run(run, b);
            body["results"] = new JsonArray(repo.ResultsOf(runId)
                .Select(r => (JsonNode?)ApiViews.Result(r, b, full: false, previous: repo.Previous(r))).ToArray());
            return Results.Json(body);
        }).AddEndpointFilter(read);

        v1.MapGet("/runs/{runId}/results/{position:int}", (string runId, int position, IResultRepository repo,
            IOptions<BridgeOptions> bridge, HttpContext http) =>
        {
            if (!repo.Available) return NoHistory(repo);
            if (repo.GetResult(runId, position) is not { } r) return ApiAuth.Problem(404, $"No result {runId}/{position}.");
            return Results.Json(ApiViews.Result(r, ApiViews.BaseUrl(http, bridge.Value.PublicUrl), full: true, previous: repo.Previous(r)));
        }).AddEndpointFilter(read);

        v1.MapGet("/runs/{runId}/results/{position:int}/compare", (string runId, int position, IResultRepository repo,
            IOptions<BridgeOptions> bridge, HttpContext http) =>
        {
            if (!repo.Available) return NoHistory(repo);
            if (repo.GetResult(runId, position) is not { } r) return ApiAuth.Problem(404, $"No result {runId}/{position}.");
            var b = ApiViews.BaseUrl(http, bridge.Value.PublicUrl);
            var previous = repo.Previous(r);
            var lastPass = r.Verdict == "PASSED" ? null : repo.LastPassingBefore(r);
            return Results.Json(new JsonObject
            {
                ["result"] = ApiViews.Result(r, b, full: false, previous),
                ["previous"] = previous is null ? null : ApiViews.Result(previous, b, full: false),
                ["lastPassing"] = lastPass is null ? null : ApiViews.Result(lastPass, b, full: false),
                ["responseChangesSinceLastPass"] = ApiViews.Diff(lastPass, r)
            });
        }).AddEndpointFilter(read);

        v1.MapGet("/tests/{testKey}/history", (string testKey, int? limit, IResultRepository repo,
            IOptions<BridgeOptions> bridge, HttpContext http) =>
        {
            if (!repo.Available) return NoHistory(repo);
            var b = ApiViews.BaseUrl(http, bridge.Value.PublicUrl);
            var points = repo.History(testKey, limit ?? 50);
            return Results.Json(new JsonObject
            {
                ["testKey"] = testKey,
                ["count"] = points.Count,
                ["passRate"] = points.Count == 0 ? null : Math.Round(points.Count(p => p.Verdict == "PASSED") / (double)points.Count, 3),
                ["history"] = new JsonArray(points.Select(p => (JsonNode?)ApiViews.Point(p, b)).ToArray())
            });
        }).AddEndpointFilter(read);

        v1.MapPost("/runs/{runId}/results/{position:int}/feedback", (string runId, int position, FeedbackRequest body,
            IResultRepository repo, HttpContext http) =>
        {
            if (!repo.Available) return NoHistory(repo);
            var value = body.Helpful switch { true => 1, false => -1, null => (int?)null };
            return repo.SetFeedback(runId, position, value, "api:" + http.Client()!.Name, body.Note)
                ? Results.Json(new { runId, position, feedback = value })
                : ApiAuth.Problem(404, $"No result {runId}/{position}.");
        }).AddEndpointFilter(ApiAuth.Require(Scopes.FeedbackWrite, allowBearer: true));

        v1.MapGet("/audit", (int? limit, string? target, IResultRepository repo) =>
            repo.Available
                ? Results.Json(new JsonObject { ["entries"] = new JsonArray(repo.AuditLog(limit ?? 200, target).Select(e => (JsonNode?)ApiViews.Audit(e)).ToArray()) })
                : NoHistory(repo))
            .AddEndpointFilter(ApiAuth.Require(Scopes.AuditRead, allowBearer: true));
    }

    public sealed record FeedbackRequest(bool? Helpful, string? Note);

    private static IResult NoHistory(IResultRepository repo) => ApiAuth.Problem(503, "Run history is not available: " + repo.Status);
}
