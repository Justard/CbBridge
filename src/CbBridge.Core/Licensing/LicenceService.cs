using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CbBridge.Core.Licensing;

/// <summary>appsettings.json → "Licensing".</summary>
public sealed class LicensingOptions
{
    /// <summary>The licence key itself (better: environment variable Licensing__Key) …</summary>
    public string Key { get; set; } = "";
    /// <summary>… or a file containing it, relative to the app folder.</summary>
    public string File { get; set; } = "licence.key";
    /// <summary>Development mode does not enforce the licence unless this is true.</summary>
    public bool EnforceInDevelopment { get; set; }
}

/// <summary>
/// The bridge's licence state. Without a valid licence the bridge only shows the licence page; runs, CI, API and
/// MCP are refused. Checked against the public key built into this program (Licensing/licence-public-key.pem).
/// </summary>
public sealed class LicenceService
{
    public const string PublicKeyResource = "CbBridge.licence-public-key.pem";

    private readonly LicensingOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ECDsa? _publicKey;
    private readonly Func<DateTimeOffset> _clock;
    private string? _key;

    /// <summary>True only in release builds (-p:LicenceEnforced=true); prerelease builds never block.</summary>
    public static bool EnforcedBuild =>
#if LICENCE_ENFORCED
        true;
#else
        false;
#endif

    public LicenceService(IOptions<LicensingOptions> options, IHostEnvironment env, ILogger<LicenceService> logger)
        : this(options.Value, env, LoadEmbeddedPublicKey(), () => DateTimeOffset.UtcNow, EnforcedBuild)
    {
        var check = Check;
        if (check.AllowsUse) logger.LogInformation("Licence: {Message}", check.Message);
        else if (Enforced) logger.LogError("Licence: {Message} The bridge is locked until a valid licence key is installed.", check.Message);
        else logger.LogWarning("Licence: {Message} (not enforced: {Reason})", check.Message, NotEnforcedReason);
    }

    /// <summary>For tests: a given public key, clock and build kind.</summary>
    public LicenceService(LicensingOptions options, IHostEnvironment env, ECDsa? publicKey, Func<DateTimeOffset> clock, bool enforcedBuild = true)
    {
        _options = options;
        _env = env;
        _publicKey = publicKey;
        _clock = clock;
        _enforcedBuild = enforcedBuild;
        _key = ReadKey();
    }

    private readonly bool _enforcedBuild;

    /// <summary>Is the bridge locked without a valid key? Only in a release build, and there not in Development
    /// mode (unless EnforceInDevelopment).</summary>
    public bool Enforced => _enforcedBuild && (!_env.IsDevelopment() || _options.EnforceInDevelopment);

    /// <summary>Why the licence is not enforced, or null when it is.</summary>
    public string? NotEnforcedReason =>
        !_enforcedBuild ? "prerelease build" : Enforced ? null : "Development mode";

    public bool IsPrerelease => !_enforcedBuild;

    /// <summary>Evaluated on every call, so an expiry takes effect without a restart.</summary>
    public LicenceCheck Check => LicenceKey.Verify(_key, _publicKey, _clock(), Environment.MachineName);

    /// <summary>May runs, CI, API and MCP be used now?</summary>
    public bool AllowsUse => !Enforced || Check.AllowsUse;

    public string MachineName => Environment.MachineName;

    /// <summary>Installs a key from the licence page: checked first, then written to Licensing:File.</summary>
    public LicenceCheck Install(string key)
    {
        var check = LicenceKey.Verify(key, _publicKey, _clock(), Environment.MachineName);
        if (!check.AllowsUse) return check;
        System.IO.File.WriteAllText(KeyFilePath, key.Trim() + Environment.NewLine);
        _key = key.Trim();
        return check;
    }

    private string KeyFilePath => Path.IsPathRooted(_options.File) ? _options.File : Path.Combine(_env.ContentRootPath, _options.File);

    private string? ReadKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.Key)) return _options.Key.Trim();
        try { return System.IO.File.Exists(KeyFilePath) ? System.IO.File.ReadAllText(KeyFilePath).Trim() : null; }
        catch (IOException) { return null; }
    }

    private static ECDsa? LoadEmbeddedPublicKey()
    {
        using var stream = typeof(LicenceService).Assembly.GetManifestResourceStream(PublicKeyResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        return pem.Contains("BEGIN PUBLIC KEY") ? LicenceKey.PublicKeyFromPem(pem) : null;
    }
}
