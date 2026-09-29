namespace Mdk.Api.Indicators;

/// <summary>
/// MACD（指数平滑异同移动平均线）。
/// DIF = EMA(fast) − EMA(slow)；DEA = DIF 的 EMA(signal)；柱 = DIF − DEA。
/// 有效起点：DIF 在下标 slow−1，DEA/柱 在下标 slow+signal−2，之前为 null。
/// </summary>
public static class Macd
{
    public sealed record MacdResult(decimal?[] Dif, decimal?[] Dea, decimal?[] Hist);

    public static MacdResult Compute(
        IReadOnlyList<decimal> closes,
        int fastPeriod = 12,
        int slowPeriod = 26,
        int signalPeriod = 9)
    {
        var n = closes.Count;
        var dif = new decimal?[n];
        var dea = new decimal?[n];
        var hist = new decimal?[n];
        if (n < slowPeriod) return new MacdResult(dif, dea, hist);

        var emaFast = Ema.Compute(closes, fastPeriod);
        var emaSlow = Ema.Compute(closes, slowPeriod);

        var start = slowPeriod - 1;
        for (var i = start; i < n; i++) dif[i] = emaFast[i]! - emaSlow[i]!;

        if (n - start >= signalPeriod)
        {
            // 对 DIF 有效段做 EMA(signal)，种子取该段前 signal 个的 SMA
            decimal sum = 0;
            for (var i = start; i < start + signalPeriod; i++) sum += dif[i]!.Value;
            var deaVal = sum / signalPeriod;
            var deaStart = start + signalPeriod - 1;
            dea[deaStart] = deaVal;

            var alpha = 2m / (signalPeriod + 1);
            for (var i = deaStart + 1; i < n; i++)
            {
                deaVal += alpha * (dif[i]!.Value - deaVal);
                dea[i] = deaVal;
            }
            for (var i = deaStart; i < n; i++) hist[i] = dif[i]!.Value - dea[i]!.Value;
        }

        return new MacdResult(dif, dea, hist);
    }
}

/// <summary>
/// 布林带（Bollinger Bands）：中轨 = SMA(period)，上下轨 = 中轨 ± multiplier × 总体标准差。
/// 标准差用 DecimalMath.Sqrt（纯 decimal 牛顿迭代），有效起点在下标 period−1。
/// </summary>
public static class BollingerBands
{
    public sealed record BollingerResult(decimal?[] Upper, decimal?[] Middle, decimal?[] Lower);

    public static BollingerResult Compute(
        IReadOnlyList<decimal> closes,
        int period = 20,
        decimal stdDevMultiplier = 2.0m)
    {
        var n = closes.Count;
        var middle = Sma.Compute(closes, period);
        var upper = new decimal?[n];
        var lower = new decimal?[n];

        for (var i = period - 1; i < n; i++)
        {
            if (middle[i] is not { } mean) continue;
            decimal sqSum = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                var d = closes[j] - mean;
                sqSum += d * d;
            }
            var sigma = DecimalMath.Sqrt(sqSum / period);
            upper[i] = mean + stdDevMultiplier * sigma;
            lower[i] = mean - stdDevMultiplier * sigma;
        }
        return new BollingerResult(upper, middle, lower);
    }
}
