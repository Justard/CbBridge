using System.Text.Json.Nodes;
using CbBridge.Web.Domain;

namespace CbBridge.Web.Application.Abstractions;

/// <summary>Reading side — implemented with codebeamer legacy REST.</summary>
public interface ICodebeamerReader
{
    string Describe(string path);
    Task PingAsync(CancellationToken ct = default);
    Task<JsonNode?> GetProjectsAsync(CancellationToken ct = default);
    Task<JsonNode?> GetProjectAsync(string projectId, CancellationToken ct = default);
    Task<JsonNode?> GetCategoriesAsync(string projectId, CancellationToken ct = default);
    Task<JsonNode?> GetOutlineAsync(string categoryId, CancellationToken ct = default);
    Task<JsonNode?> GetItemAsync(string itemId, CancellationToken ct = default);
    Task<JsonNode?> GetReferencesAsync(string itemId, CancellationToken ct = default);
}

/// <summary>Writing side — implemented with codebeamer Swagger REST v3.</summary>
public interface ICodebeamerResultWriter
{
    Task<(PostState State, string Message)> PostResultAsync(ExecutionResult result, CancellationToken ct = default);
}

/// <summary>The Python framework (Flask).</summary>
public interface IFrameworkClient
{
    /// <summary>Framework address used by this user right now (chosen in the UI, default from config).</summary>
    string BaseUrl { get; }

    /// <summary>Checks the framework answers at BaseUrl. Never throws.</summary>
    Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default);

    /// <summary>Never throws for timeouts/aborts — returns a result with the matching verdict.</summary>
    Task<ExecutionResult> ExecuteAsync(TestRunPayload payload, TimeSpan httpWait, CancellationToken abort);
    Task<bool> CancelAsync(string itemId);
}

public sealed class CodebeamerException(string message) : Exception(message);
