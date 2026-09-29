namespace Mdk.Api.Indicators;

/// <summary>
/// decimal 的数学补充：.NET 的 Math 只提供 double 版本，
/// 而指标（尤其布林带标准差、t 统计量）需要开方。这里用牛顿迭代在 decimal 域完成，
/// 避免"先转 double 再转回来"造成的精度损失（初值仍用 double 近似，迭代会收敛到 decimal 精度）。
/// </summary>
public static class DecimalMath
{
    /// <summary>平方根（纯 decimal 牛顿迭代）。</summary>
    public static decimal Sqrt(decimal value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "负数不能开方");
        if (value == 0) return 0m;

        var guess = (decimal)Math.Sqrt((double)value);   // 仅作初值
        if (guess <= 0) guess = value / 2;
        for (var i = 0; i < 12; i++)
        {
            var next = (guess + value / guess) / 2;
            if (next == guess) break;
            guess = next;
        }
        return guess;
    }
}
