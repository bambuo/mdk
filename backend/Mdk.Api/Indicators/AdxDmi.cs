namespace Mdk.Api.Indicators;

/// <summary>
/// DMI/ADX（Directional Movement Index），Wilder 1978。
/// +DM/−DM/TR 做 Wilder 平滑后得到 +DI/−DI；
/// DX = 100·|+DI−(−DI)|/(+DI+(−DI))；ADX 是 DX 的再一次 Wilder 平滑（二次平滑，出信号最慢）。
/// 有效起点：+DI/−DI 在下标 period，ADX 在下标 2·period−1，之前为 null。
/// </summary>
public static class AdxDmi
{
    public sealed record AdxDmiResult(decimal?[] PlusDi, decimal?[] MinusDi, decimal?[] Adx);

    public static AdxDmiResult Compute(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        int period = 14)
    {
        var n = closes.Count;
        var plusDi = new decimal?[n];
        var minusDi = new decimal?[n];
        var adx = new decimal?[n];
        if (period < 1 || n <= period) return new AdxDmiResult(plusDi, minusDi, adx);

        // 相邻K线的方向运动与真实波幅（从第 1 根起，长度 n−1）
        var m = n - 1;
        var tr = new decimal[m];
        var plusDm = new decimal[m];
        var minusDm = new decimal[m];
        for (var i = 1; i < n; i++)
        {
            var upMove = highs[i] - highs[i - 1];
            var downMove = lows[i - 1] - lows[i];
            plusDm[i - 1] = upMove > downMove && upMove > 0 ? upMove : 0m;
            minusDm[i - 1] = downMove > upMove && downMove > 0 ? downMove : 0m;
            tr[i - 1] = Math.Max(
                highs[i] - lows[i],
                Math.Max(
                    Math.Abs(highs[i] - closes[i - 1]),
                    Math.Abs(lows[i] - closes[i - 1])));
        }

        var trSmoothed = WilderSmoothing.Rma(tr, period);
        var plusSmoothed = WilderSmoothing.Rma(plusDm, period);
        var minusSmoothed = WilderSmoothing.Rma(minusDm, period);

        // DI / DX 与 tr 子数组同下标；映射回原序列时偏移 +1
        var dxs = new decimal?[m];
        var firstValidDx = -1;
        for (var j = 0; j < m; j++)
        {
            if (trSmoothed[j] is not { } trVal || plusSmoothed[j] is not { } plusVal || minusSmoothed[j] is not { } minusVal)
                continue;
            var pdi = trVal == 0 ? 0m : 100m * plusVal / trVal;
            var mdi = trVal == 0 ? 0m : 100m * minusVal / trVal;
            plusDi[j + 1] = pdi;
            minusDi[j + 1] = mdi;
            var denom = pdi + mdi;
            dxs[j] = denom == 0 ? 0m : 100m * Math.Abs(pdi - mdi) / denom;
            if (firstValidDx < 0) firstValidDx = j;
        }

        // ADX：对 DX 有效段再做一次 Wilder 平滑
        if (firstValidDx >= 0 && m - firstValidDx >= period)
        {
            var dxValid = dxs[firstValidDx..].Select(v => v ?? 0m).ToArray();
            var adxSmoothed = WilderSmoothing.Rma(dxValid, period);
            for (var j = 0; j < adxSmoothed.Length; j++)
                if (adxSmoothed[j] is { } value)
                    adx[firstValidDx + j + 1] = value;
        }

        return new AdxDmiResult(plusDi, minusDi, adx);
    }
}
