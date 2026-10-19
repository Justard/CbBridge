using System.Security.Cryptography;
using System.Text;
using CbBridge.Core.Application.Ci;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Ci;

/// <summary>
/// HTTP API for Jenkins / any CI:
///   POST /api/ci/runs              start a run   (X-Api-Key + Authorization = CI agent's codebeamer account)
///   GET  /api/ci/runs/{id}         status, results, log (?logFrom=n for new lines only)
///   GET  /api/ci/runs/{id}/junit   JUnit XML for Jenkins' junit step
///   POST /api/ci/runs/{id}/abort   stop it
///   GET  /api/ci/runs              recent jobs
/// </summary>
public static class CiEndpoints
{
    public static void MapCiApi(this WebApplication app)
    {
        // Key in X-Api-Key (Authorization carries the CI agent's codebeamer login): Ci:ApiKey or an Api:Clients key.
        var api = app.MapGroup("/api/ci").AddEndpointFilter(RequireCiEnabled);
        var read = Api.ApiAuth.Require(CbBridge.Core.Application.Api.Scopes.RunsRead, allowBearer: false);
        var start = Api.ApiAuth.Require(CbBridge.Core.Application.Api.Scopes.RunsStart, allowBearer: false);

        api.MapPost("/runs", async (HttpContext http, CiRunRequest request, CiRunner runner) =>
        {
            var credentials = ReadCredentials(http.Request);
            if (credentials is null)
                return Problem(401, "Send the CI agent's codebeamer account in the Authorization header (Basic user:password or Bearer token).");

            var (job, status, error) = await runner.StartAsync(request, credentials, http.RequestAborted, Api.ApiAuth.Client(http)?.Name);
            if (job is null) return Problem(status, error!);

            var self = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/api/ci/runs/{job.Id}";
            return Results.Json(new { id = job.Id, state = job.State.ToString(), statusUrl = self, junitUrl = self + "/junit" }, statusCode: 202);
        }).AddEndpointFilter(start);

        api.MapGet("/runs", (CiJobStore store) =>
            Results.Json(store.All().Take(50).Select(j => new { j.Id, State = j.State.ToString(), j.Kind, j.Target, j.FrameworkUrl, j.Label, j.CreatedAt, j.FinishedAt })))
            .AddEndpointFilter(read);

        api.MapGet("/runs/{id}", (string id, int? logFrom, CiJobStore store) =>
            store.Get(id) is { } job ? Results.Json(CiJobView.From(job, logFrom ?? 0)) : Problem(404, $"No CI job {id}."))
            .AddEndpointFilter(read);

        api.MapGet("/runs/{id}/junit", (string id, CiJobStore store) =>
            store.Get(id) is { } job ? Results.Content(JUnitReport.Write(job), "application/xml", Encoding.UTF8) : Problem(404, $"No CI job {id}."))
            .AddEndpointFilter(read);

        api.MapPost("/runs/{id}/abort", (string id, CiJobStore store) =>
        {
            if (store.Get(id) is not { } job) return Problem(404, $"No CI job {id}.");
            if (!job.IsDone) { job.Abort.Cancel(); job.AddLog("abort requested via API"); }
            return Results.Json(new { id, state = job.State.ToString() });
        }).AddEndpointFilter(start);
    }

    /// <summary>API off unless Ci:Enabled (404).</summary>
    private static async ValueTask<object?> RequireCiEnabled(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var options = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<CiOptions>>().Value;
        return options.Enabled ? await next(ctx) : Problem(404, "The CI API is disabled (Ci:Enabled = false).");
    }

    private static CiCredentials? ReadCredentials(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header[7..].Trim();
            return token.Length > 0 ? new CiCredentials("", token, true) : null;
        }
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon > 0) return new CiCredentials(decoded[..colon], decoded[(colon + 1)..], false);
            }
            catch (FormatException) { }
        }
        return null;
    }

    private static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
