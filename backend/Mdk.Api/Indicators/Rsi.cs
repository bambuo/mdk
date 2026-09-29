namespace Mdk.Api.Indicators;

/// <summary>
/// RSI（Relative Strength Index），Wilder 1978。
/// 涨幅均值与跌幅均值分别做 Wilder 平滑（周期 14），RSI = 100 − 100/(1+RS)。
/// 输出与 closes 等长；首个有效值在下标 period 处（需要 period 个涨跌差），之前为 null。
/// </summary>
public static class Rsi
{
    public static decimal?[] Compute(IReadOnlyList<decimal> closes, int period = 14)
    {
        var n = closes.Count;
        var result = new decimal?[n];
        if (period < 1 || n <= period) return result;

        var gains = new decimal[n];
        var losses = new decimal[n];
        for (var i = 1; i < n; i++)
        {
            var change = closes[i] - closes[i - 1];
            gains[i] = change > 0 ? change : 0m;
            losses[i] = change < 0 ? -change : 0m;
        }

        decimal avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= period; i++)
        {
            avgGain += gains[i];
            avgLoss += losses[i];
        }
        avgGain /= period;
        avgLoss /= period;
        result[period] = Value(avgGain, avgLoss);

        for (var i = period + 1; i < n; i++)
        {
            avgGain = (avgGain * (period - 1) + gains[i]) / period;
            avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
            result[i] = Value(avgGain, avgLoss);
        }
        return result;
    }

    private static decimal Value(decimal avgGain, decimal avgLoss)
    {
        // 无下跌（含全平盘的退化情形）时按惯例给出满值
        if (avgLoss == 0m) return 100m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }
}

/// <summary>
/// ATR（Average True Range），Wilder 1978。
/// TR = max(H−L, |H−Cprev|, |L−Cprev|)（首根取 H−L），再做 Wilder 平滑。
/// 首个有效值在下标 period−1 处。
/// </summary>
public static class Atr
{
    public static decimal?[] Compute(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        int period = 14)
    {
        var n = closes.Count;
        var result = new decimal?[n];
        if (period < 1 || n < period) return result;

        var tr = new decimal[n];
        tr[0] = highs[0] - lows[0];
        for (var i = 1; i < n; i++)
            tr[i] = Math.Max(
                highs[i] - lows[i],
                Math.Max(
                    Math.Abs(highs[i] - closes[i - 1]),
                    Math.Abs(lows[i] - closes[i - 1])));

        decimal sum = 0;
        for (var i = 0; i < period; i++) sum += tr[i];
        var atr = sum / period;
        result[period - 1] = atr;

        for (var i = period; i < n; i++)
        {
            atr = (atr * (period - 1) + tr[i]) / period;
            result[i] = atr;
        }
        return result;
    }
}
