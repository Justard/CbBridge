using System.Security.Cryptography;
using CbBridge.Core.Licensing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CbBridge.Tests;

public static class LicenceTests
{
    private static readonly ECDsa Owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ECDsa OwnerPublic = LicenceKey.PublicKeyFromPem(Owner.ExportSubjectPublicKeyInfoPem());
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Key(DateTimeOffset? expires = null, params string[] hosts) => LicenceKey.Issue(new LicenceGrant
    {
        Id = "L-1", Licensee = "Test GmbH", IssuedAt = Now.AddDays(-1), ExpiresAt = expires, Hosts = hosts
    }, Owner);

    private static LicenceState State(string? key, ECDsa? pub = null, string host = "BENCH-1") =>
        LicenceKey.Verify(key, pub ?? OwnerPublic, Now, host).State;

    [Test] public static void ValidKey() => Assert.Equal(LicenceState.Valid, State(Key(Now.AddDays(365))));
    [Test] public static void NoExpiryIsValid() => Assert.Equal(LicenceState.Valid, State(Key()));
    [Test] public static void ExpiringSoonStillAllowsUse()
    {
        var check = LicenceKey.Verify(Key(Now.AddDays(10)), OwnerPublic, Now, "x");
        Assert.Equal(LicenceState.ExpiringSoon, check.State);
        Assert.True(check.AllowsUse);
    }
    [Test] public static void Expired() => Assert.Equal(LicenceState.Expired, State(Key(Now.AddSeconds(-1))));
    [Test] public static void BoundToHost() => Assert.Equal(LicenceState.Valid, State(Key(null, "bench-1"), host: "BENCH-1"));
    [Test] public static void WrongHost() => Assert.Equal(LicenceState.WrongHost, State(Key(null, "OTHER"), host: "BENCH-1"));
    [Test] public static void Missing() => Assert.Equal(LicenceState.Missing, State("  "));
    [Test] public static void NoPublicKeyInBuild() => Assert.Equal(LicenceState.NoPublicKey, LicenceKey.Verify(Key(), null, Now, "x").State);
    [Test] public static void Garbage() => Assert.Equal(LicenceState.Invalid, State("hello"));

    [Test]
    public static void ChangedPayloadIsRejected()
    {
        // a licensee editing the payload (e.g. moving the expiry) breaks the signature
        var parts = Key(Now.AddDays(1)).Split('.');
        var forged = LicenceKey.Issue(new LicenceGrant { Id = "L-1", Licensee = "Test GmbH", IssuedAt = Now }, ECDsa.Create(ECCurve.NamedCurves.nistP256)).Split('.');
        Assert.Equal(LicenceState.Invalid, State($"{parts[0]}.{forged[1]}.{parts[2]}"));
    }

    [Test]
    public static void KeyFromAnotherOwnerIsRejected()
    {
        var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = LicenceKey.Issue(new LicenceGrant { Id = "x", Licensee = "Pirate", IssuedAt = Now }, stranger);
        Assert.Equal(LicenceState.Invalid, State(key));
    }

    [Test]
    public static void ServiceEnforcesOutsideDevelopmentOnly()
    {
        var prod = new LicenceService(new LicensingOptions(), new Env("Production"), OwnerPublic, () => Now);
        var dev = new LicenceService(new LicensingOptions(), new Env("Development"), OwnerPublic, () => Now);
        var strictDev = new LicenceService(new LicensingOptions { EnforceInDevelopment = true }, new Env("Development"), OwnerPublic, () => Now);
        Assert.False(prod.AllowsUse, "production without key");
        Assert.True(dev.AllowsUse, "development without key");
        Assert.False(strictDev.AllowsUse, "development with EnforceInDevelopment");
        var licensed = new LicenceService(new LicensingOptions { Key = Key(Now.AddDays(100)) }, new Env("Production"), OwnerPublic, () => Now);
        Assert.True(licensed.AllowsUse, "production with key");
    }

    [Test]
    public static void PrereleaseBuildNeverBlocks()
    {
        var prerelease = new LicenceService(new LicensingOptions { EnforceInDevelopment = true }, new Env("Production"), OwnerPublic, () => Now, enforcedBuild: false);
        Assert.True(prerelease.AllowsUse, "no key, production, prerelease build");
        Assert.Equal("prerelease build", prerelease.NotEnforcedReason);
        Assert.Equal(LicenceState.Missing, prerelease.Check.State, "state still shown");
        Assert.False(LicenceService.EnforcedBuild, "test build is a prerelease build");
    }

    [Test]
    public static void ExpiryTakesEffectWithoutRestart()
    {
        var clock = Now;
        var service = new LicenceService(new LicensingOptions { Key = Key(Now.AddDays(1)) }, new Env("Production"), OwnerPublic, () => clock);
        Assert.True(service.AllowsUse, "before");
        clock = Now.AddDays(2);
        Assert.False(service.AllowsUse, "after expiry");
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
