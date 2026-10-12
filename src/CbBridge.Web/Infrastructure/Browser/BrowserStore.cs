using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace CbBridge.Web.Infrastructure.Browser;

/// <summary>
/// Per-user settings kept in the user's own browser (localStorage, encrypted by ASP.NET Core).
/// Survives reloads and restarts; each browser/PC has its own copy. Never throws: a missing or
/// unreadable value (e.g. after the app's keys changed) simply comes back as default.
/// </summary>
public sealed class BrowserStore(ProtectedLocalStorage storage, ILogger<BrowserStore> logger)
{
    private const string Prefix = "cbbridge.";

    public async Task<T?> LoadAsync<T>(string key)
    {
        try
        {
            var result = await storage.GetAsync<T>(Prefix + key);
            return result.Success ? result.Value : default;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not read browser setting {Key}; using default", key);
            return default;
        }
    }

    public async Task SaveAsync<T>(string key, T value)
    {
        try { await storage.SetAsync(Prefix + key, value!); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not save browser setting {Key}", key); }
    }
}
