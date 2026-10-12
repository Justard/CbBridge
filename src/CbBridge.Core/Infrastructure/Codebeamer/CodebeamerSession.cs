using System.Net.Http.Headers;
using System.Text;

namespace CbBridge.Core.Infrastructure.Codebeamer;

/// <summary>
/// Per-user (per Blazor circuit) login. Credentials live only on the server, in memory,
/// and are gone when the browser tab closes or the user signs out.
/// </summary>
public sealed class CodebeamerSession
{
    private string _secret = "";
    private bool _isToken;

    public string BaseUrl { get; private set; } = "";
    public string User { get; private set; } = "";
    public bool IsSignedIn { get; private set; }

    public void Configure(string baseUrl, string user, string secret, bool isToken)
    {
        BaseUrl = baseUrl.Trim().TrimEnd('/');
        User = user.Trim();
        _secret = secret;
        _isToken = isToken;
    }

    public void MarkSignedIn() => IsSignedIn = true;

    public void SignOut()
    {
        _secret = "";
        IsSignedIn = false;
    }

    public void Authorize(HttpRequestMessage request)
    {
        request.Headers.Authorization = _isToken
            ? new AuthenticationHeaderValue("Bearer", _secret)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{_secret}")));
    }
}
