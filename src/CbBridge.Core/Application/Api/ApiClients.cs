using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Application.Api;

/// <summary>What a client may do. A client gets only the scopes it needs.</summary>
public static class Scopes
{
    public const string RunsStart = "runs:start";       // start CI runs (with its own codebeamer account)
    public const string RunsRead = "runs:read";         // CI job status, JUnit
    public const string HistoryRead = "history:read";   // runs, results, test history, comparisons
    public const string FeedbackWrite = "feedback:write"; // rate AI comments
    public const string AuditRead = "audit:read";       // who wrote what to codebeamer
    public static readonly string[] All = { RunsStart, RunsRead, HistoryRead, FeedbackWrite, AuditRead };
}

public sealed class ApiClientOptions
{
    public string Name { get; set; } = "";
    /// <summary>The key itself — better set via environment variable (Api__Clients__0__Key) …</summary>
    public string Key { get; set; } = "";
    /// <summary>… or only its SHA-256 (hex), so the config file holds no secret: `echo -n KEY | sha256sum`.</summary>
    public string KeySha256 { get; set; } = "";
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public bool Disabled { get; set; }

    /// <summary>
    /// Optional codebeamer account this client starts runs with (MCP start_run). Keeps codebeamer credentials on
    /// the server — an AI agent never sees them. Set via environment variables. Token wins over password.
    /// </summary>
    public string CodebeamerUser { get; set; } = "";
    public string CodebeamerPassword { get; set; } = "";
    public string CodebeamerToken { get; set; } = "";
}

/// <summary>appsettings.json → "Api": keys for Jenkins, n8n, MCP clients, scripts — one per client.</summary>
public sealed class ApiOptions
{
    /// <summary>Off by default: /api/v1 and /mcp answer 404 until switched on.</summary>
    public bool Enabled { get; set; }
    public List<ApiClientOptions> Clients { get; set; } = new();
}

/// <summary>appsettings.json → "Bridge": general settings.</summary>
public sealed class BridgeOptions
{
    /// <summary>Address people and systems use to reach the bridge, e.g. https://bridge.example.com — for links in
    /// events, MCP answers and AI callbacks.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>/metrics without an API key (for a monitoring server inside the network). False = needs a key with history:read.</summary>
    public bool MetricsPublic { get; set; }
}

public sealed record ApiClient(string Name, IReadOnlySet<string> Scopes, Ci.CiCredentials? Codebeamer = null)
{
    public bool Can(string scope) => Scopes.Contains(scope);
}

/// <summary>
/// Finds the client for a presented key (constant-time compare). The old single key Ci:ApiKey still works as
/// client "ci" with runs:start + runs:read.
/// </summary>
public sealed class ApiClientRegistry(IOptionsMonitor<ApiOptions> api, IOptionsMonitor<Ci.CiOptions> ci)
{
    public ApiClient? Find(string? presentedKey)
    {
        if (string.IsNullOrWhiteSpace(presentedKey)) return null;
        var presented = Encoding.UTF8.GetBytes(presentedKey.Trim());
        var presentedHash = Convert.ToHexString(SHA256.HashData(presented)).ToLowerInvariant();

        foreach (var c in api.CurrentValue.Clients.Where(c => !c.Disabled && !string.IsNullOrWhiteSpace(c.Name)))
        {
            var match = !string.IsNullOrEmpty(c.Key)
                ? CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(c.Key))
                : !string.IsNullOrEmpty(c.KeySha256) &&
                  CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presentedHash),
                      Encoding.ASCII.GetBytes(c.KeySha256.Trim().ToLowerInvariant()));
            if (match) return new ApiClient(c.Name, c.Scopes.Select(s => s.Trim().ToLowerInvariant()).ToHashSet(), CredentialsOf(c));
        }

        var legacy = ci.CurrentValue.ApiKey;
        if (!string.IsNullOrEmpty(legacy) && CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(legacy)))
            return new ApiClient("ci", new HashSet<string> { Scopes.RunsStart, Scopes.RunsRead });
        return null;
    }

    private static Ci.CiCredentials? CredentialsOf(ApiClientOptions c) =>
        !string.IsNullOrWhiteSpace(c.CodebeamerToken) ? new Ci.CiCredentials("(token)", c.CodebeamerToken, true)
        : !string.IsNullOrWhiteSpace(c.CodebeamerUser) && !string.IsNullOrEmpty(c.CodebeamerPassword)
            ? new Ci.CiCredentials(c.CodebeamerUser, c.CodebeamerPassword, false)
            : null;

    /// <summary>Config problems worth a startup warning (unknown scopes, clients without key, duplicate names).</summary>
    public IEnumerable<string> Problems()
    {
        var clients = api.CurrentValue.Clients;
        foreach (var c in clients)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) yield return "a client has no Name";
            if (string.IsNullOrEmpty(c.Key) && string.IsNullOrEmpty(c.KeySha256)) yield return $"client '{c.Name}' has neither Key nor KeySha256";
            foreach (var s in c.Scopes.Where(s => !Scopes.All.Contains(s.Trim().ToLowerInvariant())))
                yield return $"client '{c.Name}': unknown scope '{s}' (known: {string.Join(", ", Scopes.All)})";
        }
        foreach (var dup in clients.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"client name '{dup.Key}' is used {dup.Count()} times";
    }
}
