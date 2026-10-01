using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>
/// 成交量分布（Volume Profile）结果：把窗口内的成交量按价格分箱，回答"哪个价位沉淀了最多成交"。
///
/// 口径（显式）：每根K线的成交量在其 [low, high] 区间内**均匀分摊**到所覆盖的分箱。
/// 真值需要逐笔成交（tick）数据；1 分钟K线的单根区间很窄，均摊是可接受的近似，但它会**平滑**分布——
/// 因此 HVN 的峰值会被低估、LVN 的谷底会被填高。此处按近似处理并在界面标注来源，不外推为精确成本区。
/// </summary>
public sealed record VolumeProfileResult(
    /// <summary>成交量最大价（POC，分箱中心）</summary>
    decimal Poc,
    /// <summary>价值区上沿（自 POC 向两侧扩展到 70% 成交量为止的分箱边界）</summary>
    decimal VaHigh,
    /// <summary>价值区下沿</summary>
    decimal VaLow,
    /// <summary>高量节点（分箱量 ≥ 1.5×均值），按量降序</summary>
    IReadOnlyList<decimal> Hvn,
    /// <summary>低量节点（分箱量 ≤ 0.5×均值，且落在价值区内），按量升序</summary>
    IReadOnlyList<decimal> Lvn,
    decimal BinSize,
    /// <summary>参与计算的K线根数</summary>
    int Bars,
    long FromTime,
    long ToTime,
    decimal TotalVolume);

/// <summary>成交量分布的纯计算（可独立测试）。</summary>
public static class VolumeProfile
{
    /// <summary>价值区占比（行业惯用 70%）。</summary>
    public const decimal ValueAreaShare = 0.70m;

    /// <summary>高量节点阈值（均值倍数）。</summary>
    public const decimal HvnMultiple = 1.5m;

    /// <summary>低量节点阈值（均值倍数）。</summary>
    public const decimal LvnMultiple = 0.5m;

    public static VolumeProfileResult? Compute(IReadOnlyList<Candle> candles, int bins = 100)
    {
        if (candles.Count < 10 || bins < 10) return null;
        var high = candles.Max(c => c.High);
        var low = candles.Min(c => c.Low);
        if (high <= low) return null;
        var binSize = (high - low) / bins;
        if (binSize <= 0) return null;

        var volumes = new decimal[bins];
        var total = 0m;
        foreach (var candle in candles)
        {
            if (candle.Volume <= 0) continue;
            var from = Bin(candle.Low, low, binSize, bins);
            var to = Bin(candle.High, low, binSize, bins);
            var share = candle.Volume / (to - from + 1);
            for (var i = from; i <= to; i++) volumes[i] += share;
            total += candle.Volume;
        }

        if (total <= 0) return null;

        var pocBin = 0;
        for (var i = 1; i < bins; i++)
            if (volumes[i] > volumes[pocBin]) pocBin = i;

        // 价值区：自 POC 向两侧扩展，每次并入量更大的一侧，直到覆盖 70% 成交量
        var (vaFrom, vaTo) = ValueArea(volumes, pocBin, total);
        var mean = total / bins;

        var hvn = HighVolumeNodes(volumes, low, binSize, bins, mean);

        var lvn = Enumerable.Range(vaFrom, vaTo - vaFrom + 1)
            .Where(i => volumes[i] <= LvnMultiple * mean)
            .OrderBy(i => volumes[i])
            .Take(2)
            .Select(i => Center(low, binSize, i))
            .ToList();

        return new VolumeProfileResult(
            Poc: Center(low, binSize, pocBin),
            VaHigh: low + (vaTo + 1) * binSize,
            VaLow: low + vaFrom * binSize,
            Hvn: hvn,
            Lvn: lvn,
            BinSize: Math.Round(binSize, 8),
            Bars: candles.Count,
            FromTime: candles[0].Time,
            ToTime: candles[^1].Time,
            TotalVolume: total);
    }

    /// <summary>
    /// 高量节点：取量最大的分箱，把与它**连成一片**的超阈值区间整体划走，再取下一个——
    /// 否则同一个峰（宽度可达十几个分箱）会占满全部名额，双峰行情只会报出一个峰。
    /// 节点的价位取该片区的**成交量加权中心**（平台型峰的中心才是交易者认的那个价）。
    /// </summary>
    private static List<decimal> HighVolumeNodes(decimal[] volumes, decimal low, decimal binSize, int bins, decimal mean)
    {
        var nodes = new List<decimal>();
        var used = new bool[bins];
        while (nodes.Count < 3)
        {
            var best = -1;
            for (var i = 0; i < bins; i++)
                if (!used[i] && volumes[i] >= HvnMultiple * mean && (best < 0 || volumes[i] > volumes[best])) best = i;
            if (best < 0) break;

            var left = best;
            while (left - 1 >= 0 && !used[left - 1] && volumes[left - 1] >= HvnMultiple * mean) left--;
            var right = best;
            while (right + 1 < bins && !used[right + 1] && volumes[right + 1] >= HvnMultiple * mean) right++;

            var total = 0m;
            var weighted = 0m;
            for (var i = left; i <= right; i++)
            {
                total += volumes[i];
                weighted += volumes[i] * Center(low, binSize, i);
                used[i] = true;
            }

            if (total > 0) nodes.Add(Math.Round(weighted / total, 8));
        }

        return nodes;
    }

    private static (int From, int To) ValueArea(decimal[] volumes, int pocBin, decimal total)
    {
        var target = total * ValueAreaShare;
        var from = pocBin;
        var to = pocBin;
        var covered = volumes[pocBin];
        while (covered < target && (from > 0 || to < volumes.Length - 1))
        {
            var left = from > 0 ? volumes[from - 1] : -1m;
            var right = to < volumes.Length - 1 ? volumes[to + 1] : -1m;
            if (right >= left)
            {
                to++;
                covered += right;
            }
            else
            {
                from--;
                covered += left;
            }
        }

        return (from, to);
    }

    private static int Bin(decimal price, decimal low, decimal binSize, int bins) =>
        Math.Clamp((int)((price - low) / binSize), 0, bins - 1);

    private static decimal Center(decimal low, decimal binSize, int index) =>
        Math.Round(low + (index + 0.5m) * binSize, 8);
}
