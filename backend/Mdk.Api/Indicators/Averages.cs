namespace Mdk.Api.Indicators;

/// <summary>
/// Wilder 平滑（又称 RMA / SMMA），出自 J. Welles Wilder 1978 年
/// 《New Concepts in Technical Trading Systems》。
///
/// 递推公式（n 为周期）：
///   种子：MA_n     = SMA(前 n 个值)
///   之后：MA_t     = (MA_{t-1} × (n−1) + x_t) / n
///
/// 等价于平滑系数 α = 1/n 的 EMA（注意：与 span=n 的 EMA 不是一回事，
/// Wilder(n) 的平滑程度约等于 SMA(2n−1)，明显更迟钝）。
///
/// 全部派生指标（RSI/ATR/ADX）均基于本平滑。
/// 数值类型为 decimal（价格与指标不做二进制浮点近似）；
/// 未定义段用 null 表示（decimal 没有 NaN）。
/// </summary>
public static class WilderSmoothing
{
    public static decimal?[] Rma(IReadOnlyList<decimal> values, int period)
    {
        var n = values.Count;
        var result = new decimal?[n];
        if (period < 1 || n < period) return result;

        decimal sum = 0;
        for (var i = 0; i < period; i++) sum += values[i];
        var ma = sum / period;
        result[period - 1] = ma;

        var weight = (decimal)(period - 1) / period;
        for (var i = period; i < n; i++)
        {
            ma = ma * weight + values[i] / period;
            result[i] = ma;
        }
        return result;
    }
}

/// <summary>简单移动平均，前 period−1 个点为 null。</summary>
public static class Sma
{
    public static decimal?[] Compute(IReadOnlyList<decimal> values, int period)
    {
        var n = values.Count;
        var result = new decimal?[n];
        if (period < 1 || n < period) return result;

        decimal sum = 0;
        for (var i = 0; i < n; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            if (i >= period - 1) result[i] = sum / period;
        }
        return result;
    }
}

/// <summary>指数移动平均：种子为前 period 个值的 SMA，之后 EMA += α(x − EMA)，α = 2/(period+1)。</summary>
public static class Ema
{
    public static decimal?[] Compute(IReadOnlyList<decimal> values, int period)
    {
        var n = values.Count;
        var result = new decimal?[n];
        if (period < 1 || n < period) return result;

        decimal sum = 0;
        for (var i = 0; i < period; i++) sum += values[i];
        var ema = sum / period;
        result[period - 1] = ema;

        var alpha = 2m / (period + 1);
        for (var i = period; i < n; i++)
        {
            ema += alpha * (values[i] - ema);
            result[i] = ema;
        }
        return result;
    }
}
