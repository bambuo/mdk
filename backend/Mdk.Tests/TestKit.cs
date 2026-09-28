namespace Mdk.Tests;

/// <summary>
/// 自带的零依赖测试机制：轻量用例运行器 + 断言助手。
/// 运行：dotnet run --project backend/Mdk.Tests；全部通过退出码 0，存在失败退出码 1。
/// </summary>
public sealed class TestKit
{
    private readonly List<(string Name, Action Test)> _cases = [];
    private readonly List<string> _failed = [];

    public void Case(string name, Action test) => _cases.Add((name, test));

    public int RunAll()
    {
        foreach (var (name, test) in _cases)
        {
            try
            {
                test();
                Console.WriteLine($"  ✓ {name}");
            }
            catch (Exception ex)
            {
                _failed.Add(name);
                Console.WriteLine($"  ✗ {name}");
                Console.WriteLine($"      {ex.Message}");
            }
        }
        Console.WriteLine();
        var passed = _cases.Count - _failed.Count;
        Console.WriteLine($"结果：通过 {passed}，失败 {_failed.Count}，共 {_cases.Count}");
        return _failed.Count == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool condition, string message = "断言失败")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message = "断言失败")
    {
        if (condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"期望 {expected}，实际 {actual}");
    }

    public static void NotEqual<T>(T expected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"不应等于 {expected}");
    }

    /// <summary>按小数位四舍五入后比较。</summary>
    public static void Equal(double expected, double actual, int precision, string? message = null)
    {
        var scale = Math.Pow(10, precision);
        if (Math.Round(expected * scale) != Math.Round(actual * scale))
            throw new InvalidOperationException(message ?? $"期望 {expected}（{precision} 位小数），实际 {actual}");
    }

    public static void Null<T>(T? value, string? message = null) where T : struct =>
        True(!value.HasValue, message ?? $"期望 null，实际 {value}");

    public static void NotNull<T>(T? value, string? message = null) where T : struct =>
        True(value.HasValue, message ?? "期望非 null");

    public static void Nan(double value, string? message = null) =>
        True(double.IsNaN(value), message ?? $"期望 NaN，实际 {value}");

    public static void NotNan(double value, string? message = null) =>
        True(!double.IsNaN(value), message ?? $"期望非 NaN，实际 {value}");

    public static void InRange(double actual, double low, double high, string? message = null) =>
        True(actual >= low && actual <= high, message ?? $"期望在 [{low}, {high}] 内，实际 {actual}");

    public static void Contains(string expectedSubstring, string actual, string? message = null) =>
        True(actual.Contains(expectedSubstring, StringComparison.Ordinal),
            message ?? $"期望 “{actual}” 包含 “{expectedSubstring}”");

    public static void Contains<T>(IEnumerable<T> items, Func<T, bool> predicate, string? message = null) =>
        True(items.Any(predicate), message ?? "集合中未找到满足条件的元素");

    public static void All<T>(IEnumerable<T> items, Func<T, bool> predicate, string? message = null) =>
        True(items.All(predicate), message ?? "集合中存在不满足条件的元素");

    /// <summary>断言式遍历：对每个元素执行断言，任一断言抛出即失败。</summary>
    public static void All<T>(IEnumerable<T> items, Action<T> assertion, string? message = null)
    {
        foreach (var item in items)
        {
            try
            {
                assertion(item);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(message ?? $"元素 {item} 断言失败：{ex.Message}");
            }
        }
    }

    public static T Single<T>(IReadOnlyList<T> list, string? message = null)
    {
        Equal(1, list.Count, message ?? $"期望恰好 1 个元素，实际 {list.Count}");
        return list[0];
    }

    public static void Throws<TEx>(Action action, string? message = null) where TEx : Exception
    {
        try
        {
            action();
        }
        catch (TEx)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                message ?? $"期望异常 {typeof(TEx).Name}，实际 {ex.GetType().Name}：{ex.Message}");
        }
        throw new InvalidOperationException(message ?? $"期望异常 {typeof(TEx).Name}，但未抛出");
    }
}
