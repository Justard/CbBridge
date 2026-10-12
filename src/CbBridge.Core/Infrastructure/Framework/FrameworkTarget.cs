using CbBridge.Core.Application;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Infrastructure.Framework;

/// <summary>
/// Which framework (test system) this user talks to. Per browser tab; starts from appsettings,
/// changed in the UI ("Test system"). The port differs per test system, so it is a user choice.
/// </summary>
public sealed class FrameworkTarget(IOptions<FrameworkOptions> options)
{
    private string? _baseUrl;

    public string BaseUrl => _baseUrl ?? options.Value.BaseUrl.TrimEnd('/');

    /// <summary>Accepts "http://host:port" (or https). Returns an error text, or null when applied.</summary>
    public string? TrySet(string? url)
    {
        var normalized = Normalize(url);
        if (normalized is null)
            return "Enter a full address like http://localhost:5001 or http://10.0.0.12:8080";
        _baseUrl = normalized;
        return null;
    }

    public static string? Normalize(string? url)
    {
        var s = (url ?? "").Trim().TrimEnd('/');
        if (s.Length > 0 && !s.Contains("://")) s = "http://" + s; // "localhost:5001" → http://localhost:5001
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
    }
}
