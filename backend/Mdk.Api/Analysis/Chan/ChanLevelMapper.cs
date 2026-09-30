using Mdk.Api.Models;

namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 级别结构映射：把某一周期的缠论结果映射为"按时间窗口过滤 + 带嵌套关系"的中枢列表。
///
/// 用途：一次分析同时给出 高周期 / 本级别 / 次级别 三个级别的中枢，前端在同一张图上叠加显示
/// （大级别中枢=宽区间，次级别中枢=细粒度区间），并在本级别中枢上标注它与上下级别的关系。
///
/// 说明：这是"多级别结构叠加"，不是严格的"级别递归构成"——
/// 原文的"本级别中枢由次级别走势类型构成"需要线段/走势类型分类（流派分歧较大），
/// 本模块用可验证的边界先交付"级别归属"这一可观察价值：中枢是否落在更大级别中枢内、
/// 内部包含多少个更小级别中枢。
/// </summary>
public static class ChanLevelMapper
{
    /// <summary>
    /// 把某级别的中枢映射为时间区间列表（只保留与 [fromTime, toTime] 有交集者，取最近 maxPivots 个）。
    /// 数据不足以覆盖窗口时按实际覆盖返回（CoverageFromTime 用于前端提示）。
    /// </summary>
    public static IReadOnlyList<ChanPivotInfo> ToPivotInfos(
        IReadOnlyList<Candle> candles,
        ChanResult result,
        long fromTime,
        long toTime,
        int maxPivots)
    {
        // 唯一实现：判定口径 = **与窗口有交集即保留**（不是"起终点都必须落在窗口内"）。
        // 修因（2026-09-30 审查）：缠论在更长的内部窗口上计算后整体左移到显示窗口，
        // 跨越窗口左边界的中枢其 startBar 为负，此前被 `startBar < 0` 整条丢弃——
        // 表现为图上少一段中枢带，且 300/500/1000 三种显示窗口给出的中枢不一致
        // （由 chan-repro.ts 的独立复算发现）。越界索引的时间按等间隔外推（K线连续）。
        var barSeconds = BarSeconds(candles);
        var infos = new List<ChanPivotInfo>();
        foreach (var pivot in result.Pivots)
        {
            if (pivot.StartStrokeIndex < 0 || pivot.StartStrokeIndex >= result.Strokes.Count) continue;
            if (pivot.EndStrokeIndex < 0 || pivot.EndStrokeIndex >= result.Strokes.Count) continue;

            var startBar = result.Strokes[pivot.StartStrokeIndex].StartBarIndex;
            var endBar = result.Strokes[pivot.EndStrokeIndex].EndBarIndex;
            if (endBar < 0 || startBar >= candles.Count) continue;   // 与显示窗口完全无交集

            var pFrom = TimeAt(candles, startBar, barSeconds);
            var pTo = TimeAt(candles, endBar, barSeconds);
            if (pTo < fromTime || pFrom > toTime) continue;

            infos.Add(new ChanPivotInfo(
                FromTime: pFrom,
                ToTime: pTo,
                Zg: pivot.Zg,
                Zd: pivot.Zd,
                Strokes: pivot.StrokeCount,
                IsConfirmed: pivot.IsConfirmed));
        }
        return infos.Count <= maxPivots ? infos : infos.Skip(infos.Count - maxPivots).ToList();
    }

    /// <summary>由相邻K线间隔推断周期秒数（中位数，取前 64 个差值）；不足两根时为 0。</summary>
    private static long BarSeconds(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 2) return 0;
        var deltas = new List<long>();
        for (var i = 1; i < candles.Count && deltas.Count < 64; i++)
        {
            var d = candles[i].Time - candles[i - 1].Time;
            if (d > 0) deltas.Add(d);
        }
        if (deltas.Count == 0) return 0;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }

    /// <summary>窗口内K线时间；索引越界（中枢起点在窗口左侧）时按等间隔外推。</summary>
    private static long TimeAt(IReadOnlyList<Candle> candles, int barIndex, long barSeconds) =>
        barIndex >= 0 && barIndex < candles.Count
            ? candles[barIndex].Time
            : candles[0].Time + barIndex * barSeconds;

    /// <summary>
    /// 计算级别归属（两个口径分别对应不同问题，故判据不同）：
    /// ① **在高周期中枢内**：本级别中枢与某个高周期中枢"时间相交 + 价格相交"——即它发生在更大级别的震荡区域内；
    /// ② **含次级别中枢数**：次级别中枢的**时间中点**落在本中枢区间内、且价格区间与本中枢相交
    ///    ——即"本中枢运行期间，次级别在此价格区内形成过多少个中枢"（结构活跃度）。
    ///    这里不用严格包含：次级别中枢常比本级别更宽（笔中枢近似的已知特性），严格包含会恒为 0，失去信息量。
    /// </summary>
    public static (bool InsideHigher, int LowerPivotCount) Annotate(
        ChanPivotInfo self,
        IReadOnlyList<ChanPivotInfo> higher,
        IReadOnlyList<ChanPivotInfo> lower)
    {
        var insideHigher = higher.Any(h =>
            h.FromTime <= self.ToTime && h.ToTime >= self.FromTime &&
            h.Zd <= self.Zg && h.Zg >= self.Zd);

        var lowerCount = lower.Count(l =>
        {
            var mid = l.FromTime + (l.ToTime - l.FromTime) / 2;
            if (mid < self.FromTime || mid > self.ToTime) return false;
            return l.Zd <= self.Zg && l.Zg >= self.Zd;
        });

        return (insideHigher, lowerCount);
    }

    /// <summary>次级别K线的覆盖情况：返回其实际起始时间（用于前端提示"该级别数据仅覆盖到…"）。</summary>
    public static long CoverageFromTime(IReadOnlyList<Candle> candles) =>
        candles.Count == 0 ? 0 : candles[0].Time;
}
