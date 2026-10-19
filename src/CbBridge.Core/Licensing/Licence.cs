using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CbBridge.Core.Licensing;

/// <summary>What a licence key grants. Signed by the copyright holder's private key; the bridge only has the public key.</summary>
public sealed record LicenceGrant
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("licensee")] public string Licensee { get; init; } = "";
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("issuedAt")] public DateTimeOffset IssuedAt { get; init; }
    /// <summary>Null = does not expire.</summary>
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>Machine names the bridge may run on; empty = any.</summary>
    [JsonPropertyName("hosts")] public string[] Hosts { get; init; } = Array.Empty<string>();
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

public enum LicenceState { Valid, ExpiringSoon, Missing, Invalid, Expired, WrongHost, NoPublicKey }

public sealed record LicenceCheck(LicenceState State, LicenceGrant? Grant, string Message)
{
    public bool AllowsUse => State is LicenceState.Valid or LicenceState.ExpiringSoon;
}

/// <summary>
/// Licence key format: <c>CBB1.&lt;payload&gt;.&lt;signature&gt;</c> — base64url JSON payload, ECDSA P-256 / SHA-256
/// signature over "CBB1.&lt;payload&gt;". Only the holder of the private key can make keys; changing a single
/// character of a key makes it invalid.
/// </summary>
public static class LicenceKey
{
    public const string Prefix = "CBB1";
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Issue(LicenceGrant grant, ECDsa privateKey)
    {
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(grant, Json));
        var signed = $"{Prefix}.{payload}";
        var signature = privateKey.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256);
        return $"{signed}.{B64(signature)}";
    }

    public static LicenceCheck Verify(string? key, ECDsa? publicKey, DateTimeOffset now, string machineName, int warnDays = 30)
    {
        if (publicKey is null)
            return new(LicenceState.NoPublicKey, null, "This build contains no licence public key — see docs/licensing.md (keygen).");
        if (string.IsNullOrWhiteSpace(key))
            return new(LicenceState.Missing, null, "No licence key installed.");

        var parts = key.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
            return new(LicenceState.Invalid, null, "Not a CB Test Bridge licence key.");
        byte[] payload, signature;
        try { payload = UnB64(parts[1]); signature = UnB64(parts[2]); }
        catch (FormatException) { return new(LicenceState.Invalid, null, "The licence key is damaged."); }

        if (!publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256))
            return new(LicenceState.Invalid, null, "The licence key's signature is not valid (changed, or issued for another build).");

        LicenceGrant? grant;
        try { grant = JsonSerializer.Deserialize<LicenceGrant>(payload, Json); }
        catch (JsonException) { grant = null; }
        if (grant is null || grant.Version != 1 || string.IsNullOrWhiteSpace(grant.Licensee))
            return new(LicenceState.Invalid, null, "The licence key's content is not readable by this version.");

        if (grant.Hosts.Length > 0 && !grant.Hosts.Contains(machineName, StringComparer.OrdinalIgnoreCase))
            return new(LicenceState.WrongHost, grant, $"Licensed for {string.Join(", ", grant.Hosts)}, but this machine is '{machineName}'.");
        if (grant.ExpiresAt is { } end && now >= end)
            return new(LicenceState.Expired, grant, $"The licence expired on {end:yyyy-MM-dd}.");
        if (grant.ExpiresAt is { } soon && now >= soon.AddDays(-warnDays))
            return new(LicenceState.ExpiringSoon, grant, $"Licensed to {grant.Licensee} — expires on {soon:yyyy-MM-dd} ({Math.Max(0, (int)Math.Ceiling((soon - now).TotalDays))} days).");

        return new(LicenceState.Valid, grant, $"Licensed to {grant.Licensee}" + (grant.ExpiresAt is { } e ? $" until {e:yyyy-MM-dd}" : "") + ".");
    }

    public static ECDsa PublicKeyFromPem(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
