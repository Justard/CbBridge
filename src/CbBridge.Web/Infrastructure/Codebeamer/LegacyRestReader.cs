using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CbBridge.Web.Application;
using CbBridge.Web.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace CbBridge.Web.Infrastructure.Codebeamer;

/// <summary>All reading goes through codebeamer legacy REST (/rest/...).</summary>
public sealed class LegacyRestReader(
    IHttpClientFactory httpFactory,
    CodebeamerSession session,
    IOptions<CodebeamerOptions> options) : ICodebeamerReader
{
    public string Describe(string path) => $"GET {options.Value.LegacyPrefix}{path}";

    public Task PingAsync(CancellationToken ct = default) => GetAsync(options.Value.PingPath, ct);
    public Task<JsonNode?> GetProjectsAsync(CancellationToken ct = default) => GetAsync("/projects", ct);
    public Task<JsonNode?> GetProjectAsync(string projectId, CancellationToken ct = default) => GetAsync($"/project/{projectId}", ct);
    public Task<JsonNode?> GetCategoriesAsync(string projectId, CancellationToken ct = default) => GetAsync($"/project/{projectId}/categories", ct);
    public Task<JsonNode?> GetOutlineAsync(string categoryId, CancellationToken ct = default) => GetAsync($"/category/{categoryId}/outline", ct);
    public Task<JsonNode?> GetItemAsync(string itemId, CancellationToken ct = default) => GetAsync($"/item/{itemId}", ct);
    public Task<JsonNode?> GetReferencesAsync(string itemId, CancellationToken ct = default) => GetAsync($"/item/{itemId}/references", ct);

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        var url = session.BaseUrl + options.Value.LegacyPrefix + path;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        session.Authorize(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient("codebeamer").SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CodebeamerException($"{Describe(path)} → cannot reach {session.BaseUrl}: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new CodebeamerException($"{Describe(path)} → HTTP {(int)response.StatusCode}: {Shorten(body)}");
            try
            {
                return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                throw new CodebeamerException($"{Describe(path)} → response is not JSON: {Shorten(body)}");
            }
        }
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
