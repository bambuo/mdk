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
/// 所有派生指标（RSI/ATR/ADX）均基于本平滑。
/// 输出与输入等长，前 n−1 个点为 NaN（样本不足时整段 NaN）。
/// </summary>
public static class WilderSmoothing
{
    public static double[] Rma(IReadOnlyList<double> values, int period)
    {
        var n = values.Count;
        var result = new double[n];
        Array.Fill(result, double.NaN);
        if (period < 1 || n < period) return result;

        double sum = 0;
        for (var i = 0; i < period; i++) sum += values[i];
        var ma = sum / period;
        result[period - 1] = ma;

        var weight = (double)(period - 1) / period;
        for (var i = period; i < n; i++)
        {
            ma = ma * weight + values[i] / period;
            result[i] = ma;
        }
        return result;
    }
}

/// <summary>简单移动平均，前 period−1 个点为 NaN。</summary>
public static class Sma
{
    public static double[] Compute(IReadOnlyList<double> values, int period)
    {
        var n = values.Count;
        var result = new double[n];
        Array.Fill(result, double.NaN);
        if (period < 1 || n < period) return result;

        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            if (i >= period - 1) result[i] = sum / period;
        }
        return result;
    }
}

/// <summary>指数移动平均：种子为前 period 个值的 SMA，之后 EMA += α(EMAx − EMA)，α = 2/(period+1)。</summary>
public static class Ema
{
    public static double[] Compute(IReadOnlyList<double> values, int period)
    {
        var n = values.Count;
        var result = new double[n];
        Array.Fill(result, double.NaN);
        if (period < 1 || n < period) return result;

        double sum = 0;
        for (var i = 0; i < period; i++) sum += values[i];
        var ema = sum / period;
        result[period - 1] = ema;

        var alpha = 2.0 / (period + 1);
        for (var i = period; i < n; i++)
        {
            ema += alpha * (values[i] - ema);
            result[i] = ema;
        }
        return result;
    }
}
