using Mdk.Api.Models;

namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论第六步：1/2/3 类买卖点。
///
/// 规则（v1，取最常见简化，依据写入说明，靠事后绩效检验）：
/// · 1 类买点：向下笔离开中枢且相对进入段背驰（MACD 面积比 &lt; 阈值）→ 买点；参考价为该笔低点。
/// · 2 类买点：**1 类买点所在笔之后**，向上笔完成、随后的向下笔回抽不破 1 类买点低点 → 买点。
/// · 3 类买点：向上笔突破中枢上沿 Zg 后，回抽的向下笔低点仍在 Zg 上方（不回中枢）→ 买点。
/// · 卖点镜像（1/2/3 类卖点）。
///
/// **记账口径（严格无未来函数）**：只有"不可修正"的笔（IsConfirmed）才能产生信号；
/// 信号时间 = 该笔稳定时点所在K线（StableFromBarIndex）的收盘，价格 = 该K线收盘价；
/// 结构参考极值只出现在说明里，不参与记账。
/// </summary>
public static class ChanSignals
{
    public static IReadOnlyList<ChanBuySellPoint> Detect(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<ChanStroke> strokes,
        IReadOnlyList<ChanPivot> pivots,
        double[] macdHist,
        double[] atr,
        double areaRatioThreshold,
        int warmupBars = 0,
        Func<long, long, string, bool>? subLevelConfirmed = null)
    {
        // 次级别确认：在"结构参考点 → 记账点"之间（且不超过确认窗口），次级别需完成一笔同向笔
        bool SubLevelOk(int anchorIndex, string side)
        {
            if (subLevelConfirmed is null) return true;
            if (anchorIndex < 0 || anchorIndex >= strokes.Count) return false;
            var stroke = strokes[anchorIndex];
            var referenceTime = stroke.EndBarIndex >= 0 && stroke.EndBarIndex < candles.Count
                ? candles[stroke.EndBarIndex].Time
                : 0;
            var accountingTime = SignalTime(strokes, anchorIndex, candles);
            if (referenceTime == 0 || accountingTime == 0) return false;
            return subLevelConfirmed(referenceTime, accountingTime, side);
        }

        var points = new List<ChanBuySellPoint>();
        // 结构口径的 1 类锚点：仅表示"此处结构上构成 1 类"，与是否通过可交易性校验无关（供 2 类使用）
        var buyAnchors = new HashSet<int>();
        var sellAnchors = new HashSet<int>();

        // ── 1 类买卖点：离开中枢的同向笔相对进入段背驰 ──
        foreach (var pivot in pivots)
        {
            if (IsBoundaryContaminated(pivot, strokes, warmupBars)) continue;
            if (ChanDivergenceDetector.Check(strokes, pivot, macdHist, areaRatioThreshold) is not { } dv) continue;
            if (pivot.LeavingStrokeIndex is not { } leaveIndex) continue;

            var stroke = strokes[leaveIndex];
            var isUp = dv.IsUp;
            var kind = isUp ? "1卖" : "1买";
            var side = isUp ? "sell" : "buy";
            var reference = isUp ? stroke.High : stroke.Low;
            var note = $"MACD 面积背驰 {dv.AreaRatio:0.00}，参考{(isUp ? "高点" : "低点")} {reference:0.##}";

            if (isUp) sellAnchors.Add(leaveIndex);
            else buyAnchors.Add(leaveIndex);
            if (SubLevelOk(leaveIndex, side)) 
                Add(points, candles, strokes, atr, leaveIndex, kind, side, reference, dv.AreaRatio, note);
        }

        // ── 2 类买卖点：1 类之后的首次回抽不破（只依赖 1 类的结构形态，不受其可交易性影响） ──
        foreach (var k in buyAnchors)
        {
            // 形态：向下笔(1买) → 向上笔 → 向下笔，且回抽低点不破 1 买低点
            if (k + 2 >= strokes.Count) continue;
            var a = strokes[k];
            var b = strokes[k + 1];
            var c = strokes[k + 2];
            if (a.IsUp || !b.IsUp || c.IsUp) continue;
            if (c.Low <= a.Low) continue;

            var note = $"1买后回抽不破前低 {a.Low:0.##}，回抽低点 {c.Low:0.##}";
            if (SubLevelOk(k + 2, "buy"))
                Add(points, candles, strokes, atr, k + 2, "2买", "buy", c.Low, null, note);
        }
        foreach (var k in sellAnchors)
        {
            // 形态：向上笔(1卖) → 向下笔 → 向上笔，且回抽高点不破 1 卖高点
            if (k + 2 >= strokes.Count) continue;
            var a = strokes[k];
            var b = strokes[k + 1];
            var c = strokes[k + 2];
            if (!a.IsUp || b.IsUp || !c.IsUp) continue;
            if (c.High >= a.High) continue;

            var note = $"1卖后回抽不破前高 {a.High:0.##}，回抽高点 {c.High:0.##}";
            if (SubLevelOk(k + 2, "sell"))
                Add(points, candles, strokes, atr, k + 2, "2卖", "sell", c.High, null, note);
        }

        // ── 3 类买卖点：突破中枢后回抽不回中枢（每个中枢每侧只取首个，避免同一中枢反复报点） ──
        foreach (var pivot in pivots)
        {
            if (!pivot.IsConfirmed) continue;
            if (IsBoundaryContaminated(pivot, strokes, warmupBars)) continue;
            var thirdBuyDone = false;
            var thirdSellDone = false;
            for (var i = pivot.EndStrokeIndex + 1; i + 1 < strokes.Count && !(thirdBuyDone && thirdSellDone); i++)
            {
                var leave = strokes[i];
                var pullback = strokes[i + 1];

                if (!thirdBuyDone && leave.IsUp && leave.High > pivot.Zg && !pullback.IsUp && pullback.Low > pivot.Zg)
                {
                    var note = $"突破中枢上沿 {pivot.Zg:0.##} 后回抽不回中枢，回抽低点 {pullback.Low:0.##}";
                    if (SubLevelOk(i + 1, "buy")
                        && Add(points, candles, strokes, atr, i + 1, "3买", "buy", pullback.Low, null, note))
                        thirdBuyDone = true;
                }
                if (!thirdSellDone && !leave.IsUp && leave.Low < pivot.Zd && pullback.IsUp && pullback.High < pivot.Zd)
                {
                    var note = $"跌破中枢下沿 {pivot.Zd:0.##} 后回抽不回中枢，回抽高点 {pullback.High:0.##}";
                    if (SubLevelOk(i + 1, "sell")
                        && Add(points, candles, strokes, atr, i + 1, "3卖", "sell", pullback.High, null, note))
                        thirdSellDone = true;
                }
            }
        }

        // 同一笔上至多保留一个信号（优先 1 类，其次 2/3 类），并按时间排序
        return points
            .GroupBy(p => (p.ReferenceBarIndex, p.Side))
            .Select(g => g.OrderBy(p => p.Kind).First())
            .OrderBy(p => p.Time)
            .ToList();
    }

    /// <summary>信号记账时刻：锚点笔的稳定时点K线时间（未稳定时返回 0，表示不会产生信号）。</summary>
    private static long SignalTime(IReadOnlyList<ChanStroke> strokes, int anchorIndex, IReadOnlyList<Candle> candles)
    {
        if (anchorIndex < 0 || anchorIndex >= strokes.Count) return 0;
        var s = strokes[anchorIndex];
        if (!s.IsConfirmed || s.StableFromBarIndex is not { } bar) return 0;
        return bar >= 0 && bar < candles.Count ? candles[bar].Time : 0;
    }

    /// <summary>
    /// 中枢的成形笔是否起始于分析窗口的暖机区。
    /// 该区域内的中枢其"成形三笔"可能不完整，换窗口会得到不同区间，故一律不产生信号。
    /// </summary>
    private static bool IsBoundaryContaminated(ChanPivot pivot, IReadOnlyList<ChanStroke> strokes, int warmupBars)
    {
        if (warmupBars <= 0) return false;
        if (pivot.StartStrokeIndex < 0 || pivot.StartStrokeIndex >= strokes.Count) return true;
        return strokes[pivot.StartStrokeIndex].StartBarIndex < warmupBars;
    }

    /// <summary>按记账口径落点：只接受不可修正的笔，时间取"稳定时点"那根K线。返回是否落点成功。</summary>
    private static bool Add(
        List<ChanBuySellPoint> points,
        IReadOnlyList<Candle> candles,
        IReadOnlyList<ChanStroke> strokes,
        double[] atr,
        int anchorStrokeIndex,
        string kind,
        string side,
        double referencePrice,
        double? areaRatio,
        string note)
    {
        if (anchorStrokeIndex < 0 || anchorStrokeIndex >= strokes.Count) return false;
        var stroke = strokes[anchorStrokeIndex];
        if (!stroke.IsConfirmed || stroke.StableFromBarIndex is not { } stableBar) return false;
        if (stableBar < 0 || stableBar >= candles.Count) return false;

        var bar = candles[stableBar];
        var atrValue = stableBar < atr.Length && !double.IsNaN(atr[stableBar]) && atr[stableBar] > 0
            ? atr[stableBar]
            : bar.Close * 0.01;
        var stop = side == "buy" ? referencePrice - 0.5 * atrValue : referencePrice + 0.5 * atrValue;

        // 确认时价格若已越过结构失效位，该信号按定义已不可交易（止损落到入场价反向侧）→ 丢弃
        if (side == "buy" && stop >= bar.Close) return false;
        if (side == "sell" && stop <= bar.Close) return false;

        points.Add(new ChanBuySellPoint(
            Kind: kind,
            Side: side,
            Time: bar.Time,
            Price: bar.Close,
            ReferencePrice: referencePrice,
            ReferenceBarIndex: stroke.EndBarIndex,
            StopPrice: stop,
            AreaRatio: areaRatio,
            Note: note));
        return true;
    }
}
