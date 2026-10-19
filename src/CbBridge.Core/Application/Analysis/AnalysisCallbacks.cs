using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CbBridge.Core.Application.Analysis;

/// <summary>
/// Open asynchronous analyses, waiting for the agent to call back. One random ID + token per failed test;
/// lives for the whole app because the callback arrives on a different HTTP request.
/// </summary>
public sealed class AnalysisCallbacks
{
    private readonly ConcurrentDictionary<string, (byte[] Token, TaskCompletionSource<JsonNode?> Answer)> _open = new();

    public (string Id, string Token, Task<JsonNode?> Answer) Register()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _open[id] = (Encoding.UTF8.GetBytes(token), tcs);
        return (id, token, tcs.Task);
    }

    /// <summary>Called by the callback endpoint. False = unknown/expired ID or wrong token.</summary>
    public bool Complete(string id, string? token, JsonNode? answer)
    {
        if (token is null || !_open.TryGetValue(id, out var entry)) return false;
        if (!CryptographicOperations.FixedTimeEquals(entry.Token, Encoding.UTF8.GetBytes(token))) return false;
        if (!_open.TryRemove(id, out _)) return false;
        entry.Answer.TrySetResult(answer);
        return true;
    }

    public void Forget(string id) => _open.TryRemove(id, out _);

    public int OpenCount => _open.Count;
}
