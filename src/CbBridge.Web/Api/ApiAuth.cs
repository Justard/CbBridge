using CbBridge.Core.Application.Api;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Api;

/// <summary>Who is calling: the API client found for the presented key, checked against the scope an endpoint needs.</summary>
public static class ApiAuth
{
    private const string ClientKey = "bridge.apiClient";

    /// <summary>X-Api-Key, or (where Authorization is not used for codebeamer) "Authorization: Bearer KEY".</summary>
    public static string? PresentedKey(HttpRequest request, bool allowBearer)
    {
        var key = request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(key)) return key;
        if (!allowBearer) return null;
        var auth = request.Headers.Authorization.FirstOrDefault();
        return auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    public static ApiClient? Client(this HttpContext http) => http.Items[ClientKey] as ApiClient;

    /// <summary>Endpoint filter: 401 without a known key, 403 without the scope. Stores the client for the handler.</summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(string scope, bool allowBearer) =>
        async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var client = http.RequestServices.GetRequiredService<ApiClientRegistry>().Find(PresentedKey(http.Request, allowBearer));
            if (client is null) return Problem(401, "Unknown or missing API key (X-Api-Key" + (allowBearer ? " or Authorization: Bearer" : "") + ").");
            if (!client.Can(scope)) return Problem(403, $"API client '{client.Name}' lacks the scope '{scope}'.");
            http.Items[ClientKey] = client;
            return await next(ctx);
        };

    /// <summary>Endpoint filter: the whole API group answers 404 while Api:Enabled is false.</summary>
    public static async ValueTask<object?> RequireApiEnabled(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ctx.HttpContext.RequestServices.GetRequiredService<IOptions<ApiOptions>>().Value.Enabled
            ? await next(ctx)
            : Problem(404, "The API is disabled (Api:Enabled = false).");

    public static IResult Problem(int status, string detail) => Results.Problem(statusCode: status, detail: detail);
}
