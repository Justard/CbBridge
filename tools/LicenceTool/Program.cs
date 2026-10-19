using System.Security.Cryptography;
using System.Text.Json;
using CbBridge.Core.Licensing;

// CB Test Bridge licence tool — for the copyright holder only.
//
//   keygen                         make the key pair (once): private key → your profile, public key → the source
//   issue  --licensee NAME [...]   make a licence key
//   verify KEY|FILE                check a licence key against the public key in the source
//
// The private key is encrypted with a passphrase (asked for, or CBB_LICENCE_PASSPHRASE). Keep it and a backup
// offline. Whoever has it can make licence keys; if it is lost, no new keys can be made for existing builds.

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Help(); return 0; }
    var o = Options(args.Skip(1).ToArray());
    try
    {
        return args[0] switch
        {
            "keygen" => Keygen(o),
            "issue" => Issue(o),
            "verify" => Verify(o),
            _ => Fail($"unknown command '{args[0]}'")
        };
    }
    catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException or FormatException)
    {
        return Fail(ex.Message);
    }
}

static int Keygen(Dictionary<string, List<string>> o)
{
    var privatePath = One(o, "private") ?? DefaultPrivatePath();
    var publicPath = One(o, "public") ?? Path.Combine(RepoRoot(), "src", "CbBridge.Core", "Licensing", "licence-public-key.pem");
    if (File.Exists(privatePath) && !o.ContainsKey("force"))
        return Fail($"{privatePath} exists. Keys already made — use them, or --force to replace (every issued licence key becomes invalid for new builds).");

    var passphrase = Passphrase(confirm: true);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePath))!);
    var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
    File.WriteAllText(privatePath, key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, pbe));
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem() + "\n");

    Console.WriteLine($"private key (KEEP SECRET, back it up offline): {privatePath}");
    Console.WriteLine($"public key  (commit it — it is built into the bridge): {publicPath}");
    Console.WriteLine("Next: rebuild the bridge, then `issue` licence keys.");
    return 0;
}

static int Issue(Dictionary<string, List<string>> o)
{
    var licensee = One(o, "licensee") ?? throw new ArgumentException("--licensee is required");
    DateTimeOffset? expires = One(o, "expires") is { } e ? DateTimeOffset.Parse(e + "T23:59:59Z")
        : One(o, "days") is { } d ? DateTimeOffset.UtcNow.Date.AddDays(int.Parse(d) + 1) : null;
    var grant = new LicenceGrant
    {
        Id = "L-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)),
        Licensee = licensee,
        Email = One(o, "email"),
        IssuedAt = DateTimeOffset.UtcNow,
        ExpiresAt = expires,
        Hosts = o.TryGetValue("host", out var hosts) ? hosts.ToArray() : Array.Empty<string>(),
        Notes = One(o, "notes")
    };

    var privatePath = One(o, "private") ?? DefaultPrivatePath();
    using var key = ECDsa.Create();
    key.ImportFromEncryptedPem(File.ReadAllText(privatePath), Passphrase(confirm: false));
    var licence = LicenceKey.Issue(grant, key);

    // self-check with the public half before handing it out
    using var pub = LicenceKey.PublicKeyFromPem(key.ExportSubjectPublicKeyInfoPem());
    if (!LicenceKey.Verify(licence, pub, DateTimeOffset.UtcNow, grant.Hosts.FirstOrDefault() ?? Environment.MachineName).AllowsUse)
        return Fail("self-check failed — not issued");

    if (One(o, "out") is { } outFile) { File.WriteAllText(outFile, licence + "\n"); Console.Error.WriteLine($"written to {outFile}"); }
    Console.Error.WriteLine(JsonSerializer.Serialize(grant, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(licence);
    return 0;
}

static int Verify(Dictionary<string, List<string>> o)
{
    var input = One(o, "") ?? throw new ArgumentException("give a licence key or a file");
    var licence = File.Exists(input) ? File.ReadAllText(input).Trim() : input.Trim();
    var publicPath = One(o, "public") ?? Path.Combine(RepoRoot(), "src", "CbBridge.Core", "Licensing", "licence-public-key.pem");
    using var pub = LicenceKey.PublicKeyFromPem(File.ReadAllText(publicPath));
    var check = LicenceKey.Verify(licence, pub, DateTimeOffset.UtcNow, One(o, "host") ?? Environment.MachineName);
    Console.WriteLine($"{check.State}: {check.Message}");
    if (check.Grant is not null) Console.WriteLine(JsonSerializer.Serialize(check.Grant, new JsonSerializerOptions { WriteIndented = true }));
    return check.AllowsUse ? 0 : 1;
}

static string DefaultPrivatePath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cbbridge", "licence-private.pem");

static string RepoRoot()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "CbBridge.sln"))) return dir.FullName;
    throw new IOException("run inside the CbBridge repository, or give --public");
}

static string Passphrase(bool confirm)
{
    var env = Environment.GetEnvironmentVariable("CBB_LICENCE_PASSPHRASE");
    if (!string.IsNullOrEmpty(env)) return env;
    var first = ReadHidden("passphrase for the private key: ");
    if (confirm)
    {
        if (first.Length < 12) throw new ArgumentException("use at least 12 characters");
        if (ReadHidden("repeat: ") != first) throw new ArgumentException("passphrases differ");
    }
    return first;
}

static string ReadHidden(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
        chars.Add(k.KeyChar);
    }
    Console.Error.WriteLine();
    return new string(chars.ToArray());
}

static Dictionary<string, List<string>> Options(string[] a)
{
    var o = new Dictionary<string, List<string>>();
    for (var i = 0; i < a.Length; i++)
    {
        var name = a[i].StartsWith("--") ? a[i][2..] : "";
        var value = name == "" ? a[i] : i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
        (o.TryGetValue(name, out var list) ? list : o[name] = new()).Add(value);
    }
    return o;
}

static string? One(Dictionary<string, List<string>> o, string name) => o.TryGetValue(name, out var v) ? v[^1] : null;

static int Fail(string message) { Console.Error.WriteLine("error: " + message); return 2; }

static void Help() => Console.WriteLine("""
    CB Test Bridge licence tool

      keygen  [--private FILE] [--public FILE] [--force]
              Make the key pair once. Default private key: ~/.cbbridge/licence-private.pem (encrypted).
              Default public key: src/CbBridge.Core/Licensing/licence-public-key.pem (commit it).

      issue   --licensee NAME [--email ADDR] [--expires YYYY-MM-DD | --days N] [--host MACHINE]... [--notes TEXT]
              [--private FILE] [--out FILE]
              Print a licence key. --host binds it to machine names (repeatable); none = any machine.

      verify  KEY|FILE [--public FILE] [--host MACHINE]
              Check a key against the public key in the source.

    Passphrase: asked for, or environment variable CBB_LICENCE_PASSPHRASE.
    """);
