using System.Collections.Concurrent;

namespace CbBridge.Core.Application.Execution;

/// <summary>What is running on one test system right now.</summary>
public sealed record BenchActivity(
    string Url, string Who, string Source, string Selection, DateTimeOffset Since, int Done, int Total, string? Current, string? RunId);

/// <summary>Last use of a test system.</summary>
public sealed record BenchUse(string Url, string Who, string Source, DateTimeOffset At);

/// <summary>
/// Which test system is busy, with whose run — across all browser tabs, CI jobs and agents of this bridge.
/// The console warns before you start on a busy bench; the Benches page lists them all.
/// </summary>
public sealed class BenchRegistry
{
    private readonly ConcurrentDictionary<string, BenchActivity> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BenchUse> _lastUse = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyList<BenchActivity> Active => _active.Values.OrderBy(a => a.Since).ToList();
    public IReadOnlyList<BenchUse> Recent => _lastUse.Values.OrderByDescending(u => u.At).ToList();

    public BenchActivity? BusyWith(string url) => _active.TryGetValue(Key(url), out var a) ? a : null;

    public void Begin(string url, string who, string source, string selection, int total, string? runId)
    {
        _active[Key(url)] = new BenchActivity(Key(url), who, source, selection, DateTimeOffset.Now, 0, total, null, runId);
        Changed?.Invoke();
    }

    public void Progress(string url, int done, string? current)
    {
        if (_active.TryGetValue(Key(url), out var a)) _active[Key(url)] = a with { Done = done, Current = current };
        Changed?.Invoke();
    }

    public void End(string url)
    {
        if (_active.TryRemove(Key(url), out var a)) _lastUse[Key(url)] = new BenchUse(a.Url, a.Who, a.Source, DateTimeOffset.Now);
        Changed?.Invoke();
    }

    private static string Key(string url) => url.TrimEnd('/');
}
