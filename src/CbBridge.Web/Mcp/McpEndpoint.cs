using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Core.Application.Api;
using CbBridge.Web.Api;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Mcp;

/// <summary>
/// MCP server over Streamable HTTP at /mcp — lets AI agents (Claude, IDE assistants, n8n AI nodes …) query the
/// run history and start runs with the bridge's tools. Stateless: JSON-RPC in, JSON out, no session, no SSE stream.
/// Auth: the same API keys as /api/v1 (Authorization: Bearer KEY or X-Api-Key); tools are filtered by scope.
/// Protocol: the initialize handshake (2024-11-05 … 2025-11-25). Newer clients probe "server/discover" first and
/// fall back to this handshake when it is not supported.
/// </summary>
public static class McpEndpoint
{
    private static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    private const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602;

    private const string Instructions =
        "CB Test Bridge runs codebeamer tests on hardware test benches and keeps every result. Use list_runs / " +
        "find_new_failures to find what failed, get_run for a run's tests, compare_with_last_pass and test_history to " +
        "tell a regression (passed before, fails since a firmware/test configuration change) from a flaky test, and " +
        "get_result for the exact steps and the framework's answer. start_run executes real tests on hardware — only " +
        "when the user asks. Always give the user the 'link' of results you talk about.";

    public static void MapMcp(this WebApplication app)
    {
        // two parameters, so this is not taken for a raw RequestDelegate (which would drop the returned IResult)
        app.MapPost("/mcp", (HttpContext http, CancellationToken _) => HandleAsync(http));
        // No server-initiated stream and no sessions: GET (SSE) and DELETE (end session) are not offered.
        app.MapMethods("/mcp", new[] { "GET", "DELETE" }, () => Results.StatusCode(405));
    }

    private static async Task<IResult> HandleAsync(HttpContext http)
    {
        var sp = http.RequestServices;
        if (!sp.GetRequiredService<IOptions<ApiOptions>>().Value.Enabled)
            return ApiAuth.Problem(404, "The API is disabled (Api:Enabled = false).");

        // DNS-rebinding protection: a browser page from another origin must not reach a bridge on localhost.
        var origin = http.Request.Headers.Origin.FirstOrDefault();
        if (origin is not null && Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
            !string.Equals(o.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return ApiAuth.Problem(403, "Origin not allowed.");

        var client = sp.GetRequiredService<ApiClientRegistry>().Find(ApiAuth.PresentedKey(http.Request, allowBearer: true));
        if (client is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer realm=\"cb-test-bridge\"";
            return ApiAuth.Problem(401, "API key required: Authorization: Bearer <key> (Api:Clients).");
        }

        var version = http.Request.Headers["MCP-Protocol-Version"].FirstOrDefault();
        if (version is not null && !Versions.Contains(version))
            return Results.Json(Error(null, InvalidRequest, $"Unsupported MCP-Protocol-Version {version}"), statusCode: 400);

        JsonNode? body;
        try { body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted); }
        catch (JsonException) { return Results.Json(Error(null, ParseError, "Parse error"), statusCode: 400); }

        var baseUrl = ApiViews.BaseUrl(http, sp.GetRequiredService<IOptions<BridgeOptions>>().Value.PublicUrl);
        var context = new McpContext(client, sp, baseUrl, http.RequestAborted);

        if (body is JsonArray batch)   // allowed by 2025-03-26
        {
            var answers = new JsonArray();
            foreach (var message in batch)
                if (await DispatchAsync(message as JsonObject, context) is { } answer) answers.Add(answer);
            return answers.Count == 0 ? Results.StatusCode(202) : Results.Json(answers);
        }
        var single = await DispatchAsync(body as JsonObject, context);
        return single is null ? Results.StatusCode(202) : Results.Json(single);
    }

    /// <summary>One JSON-RPC message → answer, or null for notifications and responses.</summary>
    private static async Task<JsonObject?> DispatchAsync(JsonObject? m, McpContext c)
    {
        if (m is null || m["jsonrpc"]?.ToString() != "2.0") return Error(null, InvalidRequest, "Not a JSON-RPC 2.0 message");
        var id = m["id"]?.DeepClone();
        var method = m["method"]?.ToString();
        if (method is null) return null;      // a response from the client — nothing to do
        if (id is null) return null;          // notification (e.g. notifications/initialized)
        var p = m["params"] as JsonObject ?? new JsonObject();

        try
        {
            return method switch
            {
                "initialize" => Ok(id, Initialize(p)),
                "ping" => Ok(id, new JsonObject()),
                "tools/list" => Ok(id, new JsonObject
                {
                    ["tools"] = new JsonArray(McpTools.All.Where(t => c.Client.Can(t.Scope)).Select(t => (JsonNode?)Describe(t)).ToArray())
                }),
                "tools/call" => Ok(id, await CallAsync(p, c)),
                _ => Error(id, MethodNotFound, $"Method not found: {method}")
            };
        }
        catch (ArgumentException ex) { return Error(id, InvalidParams, ex.Message); }
    }

    private static JsonObject Initialize(JsonObject p)
    {
        var asked = p["protocolVersion"]?.ToString();
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : Versions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "cb-test-bridge", ["title"] = "CB Test Bridge",
                ["version"] = typeof(McpEndpoint).Assembly.GetName().Version?.ToString() ?? "1.0"
            },
            ["instructions"] = Instructions
        };
    }

    private static JsonObject Describe(McpTool t) => new()
    {
        ["name"] = t.Name,
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["inputSchema"] = t.InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = t.Title, ["readOnlyHint"] = t.ReadOnly,
            ["destructiveHint"] = false, ["idempotentHint"] = t.ReadOnly, ["openWorldHint"] = t.Name == "start_run"
        }
    };

    private static async Task<JsonObject> CallAsync(JsonObject p, McpContext c)
    {
        var name = p["name"]?.ToString() ?? throw new ArgumentException("params.name is required");
        var tool = McpTools.All.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException($"Unknown tool: {name}");
        if (!c.Client.Can(tool.Scope))
            return ToolResult($"API client '{c.Client.Name}' lacks the scope '{tool.Scope}' for {name}.", isError: true);
        try
        {
            var result = await tool.Run(p["arguments"] as JsonObject ?? new JsonObject(), c);
            var text = result.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var answer = ToolResult(text, isError: false);
            if (result is JsonObject structured) answer["structuredContent"] = structured.DeepClone();
            return answer;
        }
        catch (ToolError ex) { return ToolResult(ex.Message, isError: true); }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    private static JsonObject Ok(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
