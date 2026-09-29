using Mdk.Api.Models;

namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论分析编排：包含处理 → 分型 → 笔 → 中枢 → 背驰/买卖点，并生成图表用稀疏序列。
/// 全部为纯函数计算，不含 I/O；无未来函数由"笔的稳定性 + 分型确认索引"保证。
/// </summary>
public static class ChanAnalyzer
{
    public static ChanResult Analyze(
        IReadOnlyList<Candle> candles,
        decimal?[] macdHist,
        decimal?[] atr,
        ChanOptions options,
        IReadOnlyList<Candle>? subLevelCandles = null,
        string? subLevelInterval = null)
    {
        var merged = ChanInclusion.Merge(candles);
        var fractals = ChanFractalDetector.Detect(merged, candles);
        var strokes = ChanStrokeBuilder.Build(fractals, options.MinMergedBarsBetween);

        // 线段模式：中枢与买卖点都建立在**线段**之上（完整缠论体系）；否则沿用笔中枢机制
        var segments = options.UseSegments ? ChanSegmentBuilder.Build(strokes) : [];
        var useSegments = options.UseSegments && segments.Count >= ChanSegmentBuilder.MinStrokes;
        var basis = useSegments ? segments.Select(seg => seg.AsStroke()).ToList() : strokes;
        var pivots = ChanPivotDetector.Detect(basis);

        // 次级别确认：取次级别（下一档周期）最近一根"已确认"的笔方向，与本级别信号方向比较
        Func<long, long, string, bool>? subLevelConfirmed = null;
        if (options.Enabled && options.RequireSubLevelConfirm && subLevelCandles is { Count: > 50 } sub && subLevelInterval is not null)
        {
            var subMerged = ChanInclusion.Merge(sub);
            var subFractals = ChanFractalDetector.Detect(subMerged, sub);
            var subStrokes = ChanStrokeBuilder.Build(subFractals, options.MinMergedBarsBetween);
            if (options.UseSegments)
            {
                // 与主级别同口径：次级别结构也用线段
                var subSegments = ChanSegmentBuilder.Build(subStrokes);
                if (subSegments.Count >= ChanSegmentBuilder.MinStrokes)
                    subStrokes = subSegments.Select(x => x.AsStroke()).ToList();
            }
            var winBars = Math.Max(1, options.SubLevelConfirmWindowBars);
            subLevelConfirmed = (referenceTime, accountingTime, side) =>
                SubLevelMatches(sub, subStrokes, referenceTime, accountingTime, side, winBars);
        }

        var points = options.Enabled
            ? ChanSignals.Detect(candles, basis, pivots, macdHist, atr, options.DivergenceAreaRatio, options.WarmupBars, subLevelConfirmed)
            : [];

        var series = BuildSeries(candles.Count, fractals, strokes, pivots);
        if (useSegments)
        {
            var segLine = new decimal?[candles.Count];
            foreach (var seg in segments)
            {
                if (seg.StartBarIndex >= 0 && seg.StartBarIndex < candles.Count) segLine[seg.StartBarIndex] = seg.StartPrice;
                if (seg.EndBarIndex >= 0 && seg.EndBarIndex < candles.Count) segLine[seg.EndBarIndex] = seg.EndPrice;
            }
            series["chanSegment"] = segLine;
        }

        // 中枢列表保持完整（摘要中的中枢数必须与图表画出的一致）；买卖点按 MaxPoints 上限取最近者
        return new ChanResult(
            Fractals: fractals,
            Strokes: strokes,
            Pivots: pivots,
            Points: TakeLast(points, options.MaxPoints),
            Series: series,
            Segments: segments,
            LevelMode: useSegments ? "segment" : "stroke");
    }

    /// <summary>取末尾最多 max 个元素（保持原有顺序）。</summary>
    private static IReadOnlyList<T> TakeLast<T>(IReadOnlyList<T> items, int max) =>
        items.Count <= max ? items : items.Skip(items.Count - max).ToList();

    /// <summary>
    /// 次级别转向确认：在"结构参考点时间 → 记账时间"之间，次级别需完成一笔与信号同向的笔，
    /// 且该笔的稳定时点不超过参考点之后 windowBars 根（次级别K线）——即次级别在该处及时转向。
    /// 只用已确认且时点不晚于记账点的笔；次级别无满足条件的笔则视为未确认（不出信号）。
    /// </summary>
    public static bool SubLevelMatches(
        IReadOnlyList<Candle> subCandles, IReadOnlyList<ChanStroke> subStrokes,
        long referenceTime, long accountingTime, string side, int windowBars)
    {
        // 次级别一根K线的时长：用相邻K线时间差推断
        long barSeconds = 0;
        if (subCandles.Count >= 2) barSeconds = Math.Max(1, subCandles[1].Time - subCandles[0].Time);
        var deadline = windowBars > 0 && barSeconds > 0 ? referenceTime + windowBars * barSeconds : accountingTime;

        foreach (var stroke in subStrokes)
        {
            if (!stroke.IsConfirmed || stroke.StableFromBarIndex is not { } bar) continue;
            if (bar < 0 || bar >= subCandles.Count) continue;
            var time = subCandles[bar].Time;
            if (time < referenceTime || time > accountingTime || time > deadline) continue;
            var isUp = stroke.IsUp;
            if (side == "buy" ? isUp : !isUp) return true;
        }
        return false;
    }

    /// <summary>生成图表用稀疏序列：笔折线（端点）、分型点、最新中枢上下沿（覆盖该中枢区间）。</summary>
    private static Dictionary<string, decimal?[]> BuildSeries(
        int barCount,
        IReadOnlyList<ChanFractal> fractals,
        IReadOnlyList<ChanStroke> strokes,
        IReadOnlyList<ChanPivot> pivots)
    {
        var stroke = NewSeries(barCount);
        foreach (var s in strokes)
        {
            if (s.StartBarIndex >= 0 && s.StartBarIndex < barCount) stroke[s.StartBarIndex] = s.StartPrice;
            if (s.EndBarIndex >= 0 && s.EndBarIndex < barCount) stroke[s.EndBarIndex] = s.EndPrice;
        }

        var top = NewSeries(barCount);
        var bottom = NewSeries(barCount);
        foreach (var f in fractals)
        {
            if (f.BarIndex < 0 || f.BarIndex >= barCount) continue;
            if (f.IsTop) top[f.BarIndex] = f.Price;
            else bottom[f.BarIndex] = f.Price;
        }

        // 中枢带：所有返回的中枢区间都填充（互相不重叠），便于叠加显示
        var zg = NewSeries(barCount);
        var zd = NewSeries(barCount);
        foreach (var p in pivots)
        {
            var from = Math.Max(0, strokes[p.StartStrokeIndex].StartBarIndex);
            var to = Math.Min(barCount - 1, strokes[p.EndStrokeIndex].EndBarIndex);
            for (var i = from; i <= to; i++)
            {
                zg[i] = p.Zg;
                zd[i] = p.Zd;
            }
        }

        return new Dictionary<string, decimal?[]>(StringComparer.Ordinal)
        {
            ["chanStroke"] = stroke,
            ["chanFractalTop"] = top,
            ["chanFractalBottom"] = bottom,
            ["chanZg"] = zg,
            ["chanZd"] = zd,
        };
    }

    private static decimal?[] NewSeries(int length) => new decimal?[length];
}
