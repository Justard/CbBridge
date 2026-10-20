using System.Diagnostics;
using System.Reflection;

namespace CbBridge.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public sealed class AssertException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string what = "condition") { if (!condition) throw new AssertException($"expected true: {what}"); }
    public static void False(bool condition, string what = "condition") { if (condition) throw new AssertException($"expected false: {what}"); }
    public static void Equal<T>(T expected, T actual, string what = "value")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{what}: expected <{expected}> but was <{actual}>");
    }
    public static void Null(object? value, string what = "value") { if (value is not null) throw new AssertException($"{what}: expected null but was <{value}>"); }
    public static T NotNull<T>(T? value, string what = "value") where T : class => value ?? throw new AssertException($"{what}: expected a value but was null");
    public static void Contains(string expected, string? actual, string what = "text")
    {
        if (actual is null || !actual.Contains(expected)) throw new AssertException($"{what}: expected to contain <{expected}> but was <{actual}>");
    }
    public static TEx Throws<TEx>(Action action) where TEx : Exception
    {
        try { action(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"expected {typeof(TEx).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"expected {typeof(TEx).Name} but nothing was thrown");
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => (Name: $"{m.DeclaringType!.Name}.{m.Name}", Method: m))
            .Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name)
            .ToList();

        int passed = 0, failed = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, method) in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
                var result = method.Invoke(target, null);
                if (result is Task task) await task;
                (target as IDisposable)?.Dispose();
                passed++;
                Console.WriteLine($"  ok    {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                failed++;
                Console.WriteLine($"  FAIL  {name}: {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertException) Console.WriteLine(inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
            }
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed, {tests.Count} total in {total.ElapsedMilliseconds} ms");
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }
}
