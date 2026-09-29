namespace Mdk.Tests;

/// <summary>
/// 自带的零依赖测试机制：轻量用例运行器 + 断言助手。
/// 运行：dotnet run --project backend/Mdk.Tests；全部通过退出码 0，存在失败退出码 1。
///
/// 数值断言以 decimal 为主（后端数值统一为 decimal），期望值可直接写普通数字字面量；
/// "未定义段"用 null 表示（decimal 无 NaN 哨兵），故提供 Nan/NotNan 的 null 语义版本。
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
    // ── 布尔与异常 ──

    public static void True(bool condition, string message = "断言失败")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>可空比较结果（如 decimal? 的 &gt; 运算）为 null 时视为失败。</summary>
    public static void True(bool? condition, string message = "断言失败")
    {
        if (condition != true)
            throw new InvalidOperationException(
                message + (condition is null ? "（比较结果为 null，即存在未定义段）" : ""));
    }

    public static void False(bool condition, string message = "断言失败")
    {
        if (condition) throw new InvalidOperationException(message);
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

    // ── 通用相等 ──

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

    // ── decimal 数值（含"未定义段=null"） ──

    /// <summary>按小数位比较（四舍五入到指定位数）。</summary>
    public static void Equal(decimal expected, decimal actual, int precision, string? message = null)
    {
        var scale = Pow10(precision);
        if (decimal.Round(expected * scale) != decimal.Round(actual * scale))
            throw new InvalidOperationException(
                message ?? $"期望 {expected}（{precision} 位小数），实际 {actual}");
    }

    /// <summary>按小数位比较；实际为 null（未定义段）时报失败。</summary>
    public static void Equal(decimal expected, decimal? actual, int precision, string? message = null)
    {
        if (actual is not { } value)
            throw new InvalidOperationException(message ?? $"期望 {expected}，实际为 null（未定义段）");
        Equal(expected, value, precision, message);
    }

    private static decimal Pow10(int precision)
    {
        decimal scale = 1m;
        for (var i = 0; i < precision; i++) scale *= 10m;
        return scale;
    }

    public static void Equal(decimal expected, decimal actual, string? message = null)
    {
        if (expected != actual) throw new InvalidOperationException(message ?? $"期望 {expected}，实际 {actual}");
    }

    public static void NotEqual(decimal expected, decimal actual, string? message = null)
    {
        if (expected == actual) throw new InvalidOperationException(message ?? $"不应等于 {expected}");
    }

    /// <summary>断言"未定义"（null）。</summary>
    public static void Nan(decimal? value, string? message = null) =>
        True(value is null, message ?? $"期望未定义(null)，实际 {value}");

    /// <summary>断言"已定义"（非 null）。</summary>
    public static void NotNan(decimal? value, string? message = null) =>
        True(value is not null, message ?? "期望已定义（非 null）");

    public static void InRange(decimal actual, decimal low, decimal high, string? message = null) =>
        True(actual >= low && actual <= high,
            message ?? $"期望在 [{low}, {high}] 内，实际 {actual}");

    public static void InRange(decimal? actual, decimal low, decimal high, string? message = null)
    {
        if (actual is not { } value)
            throw new InvalidOperationException(message ?? $"期望在 [{low}, {high}] 内，实际为 null（未定义段）");
        InRange(value, low, high, message);
    }

    // ── 集合 ──

    public static void Null<T>(T? value, string? message = null) where T : struct =>
        True(!value.HasValue, message ?? $"期望 null，实际 {value}");

    public static void NotNull<T>(T? value, string? message = null) where T : struct =>
        True(value.HasValue, message ?? "期望非 null");

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
        if (list.Count != 1)
            throw new InvalidOperationException(message ?? $"期望恰好 1 个元素，实际 {list.Count}");
        return list[0];
    }
}
