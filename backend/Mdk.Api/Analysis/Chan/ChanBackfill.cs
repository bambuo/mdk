using Mdk.Api.Domain;
using Mdk.Api.Indicators;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 历史回填：在历史K线上**逐根复算**缠论结构，产出"若当时在线会发出的信号"。
///
/// 为什么需要：绩效统计此前只能靠"部署后现场积累"，样本增长极慢（缠论 1h 约 14 个信号/月）。
/// 币安接口可提供任意长度历史K线（分页拉取），因此可以把过去数月的历史一次性沉淀为可评估样本。
///
/// 与在线口径的一致性（严格执行无未来函数）：
/// · 每根K线 i 都用"截至 i 的内部固定窗口"复算（windowBars = Chan.AnalysisBars，与在线相同）；
/// · 只取"记账时间恰好等于当前K线"的信号——更早记账的信号在更早的迭代中已产出；
/// · 次级别与高周期同样只用"时间 ≤ i 的K线"；高周期结构亦逐根复算（其次级别即本级别）；
/// · 因此回填信号与在线信号**逐字段可比**（同一套代码路径、同一窗口口径）。
///
/// 注意：回填样本是"事后一次性生成"的（存在事后挑选参数的风险），故日志用 Origin 字段区分
/// live / backfill，绩效统计可分别查看。
/// </summary>
public static class ChanBackfill
{
    /// <summary>回填得到的一条信号（字段与 AnalysisEngine 组装的 TradeSignal 对齐）。</summary>
    public sealed record SeedSignal(
        long Time,
        string Side,
        string Kind,
        decimal Price,
        decimal ReferencePrice,
        decimal StopPrice,
        decimal? AreaRatio,
        string Note,
        decimal? Adx,
        decimal? AtrPct,
        decimal? BandwidthPct,
        string? Confluence,
        int? JointScore);

    /// <summary>
    /// 逐根复算。mainHistory 需覆盖 [fromTime - windowBars, toTime]；
    /// subHistory 为次级别（次级别确认用）；htfHistory 为高周期（趋势对齐 + 级别共振用）。
    /// </summary>
    public static IReadOnlyList<SeedSignal> Replay(
        IReadOnlyList<Candle> mainHistory,
        IReadOnlyList<Candle>? subHistory,
        string? subInterval,
        IReadOnlyList<Candle>? htfHistory,
        string? htfInterval,
        ChanOptions options,
        long fromTime,
        long toTime)
    {
        var l0 = ReplayPoints(mainHistory, subHistory, subInterval, options, fromTime, toTime);

        // 高周期结构：同样逐根复算（其次级别即本级别），用于趋势对齐与级别共振
        decimal?[]? htfEma50 = null;
        long htfSeconds = 0;
        IReadOnlyList<ChanBuySellPoint> htfPoints = [];
        if (htfHistory is { Count: > 50 } && !string.IsNullOrEmpty(htfInterval))
        {
            htfEma50 = Ema.Compute(htfHistory.Select(c => c.Close).ToArray(), 50);
            htfSeconds = MarketIntervals.IntervalSeconds(htfInterval);
            htfPoints = ReplayPoints(htfHistory, mainHistory, null, options, fromTime, toTime)
                .Select(p => p.Point).ToList();
        }

        var confluenceWindow = htfSeconds > 0 ? options.ConfluenceWindowBars * htfSeconds : 0;
        var tags = confluenceWindow > 0
            ? ConfluenceTagger.Tag(htfPoints, confluenceWindow, l0.Select(p => (p.Point.Time, p.Point.Side)))
            : new Dictionary<(long, string), string>();

        return l0.Select(p => new SeedSignal(
            Time: p.Point.Time,
            Side: p.Point.Side,
            Kind: p.Point.Kind,
            Price: p.Point.Price,
            ReferencePrice: p.Point.ReferencePrice,
            StopPrice: p.Point.StopPrice,
            AreaRatio: p.Point.AreaRatio,
            Note: p.Point.Note,
            Adx: p.Adx,
            AtrPct: p.AtrPct,
            BandwidthPct: p.BandwidthPct,
            Confluence: tags.GetValueOrDefault((p.Point.Time, p.Point.Side)),
            JointScore: p.Detail.Score)).ToList();
    }

    private sealed record LevelPoint(
        ChanBuySellPoint Point,
        decimal? Adx,
        decimal? AtrPct,
        decimal? BandwidthPct,
        JointScoreDetail Detail);

    /// <summary>单级别的逐根复算（不含高周期相关内容，供 L0 与 L1 复用）。</summary>
    private static List<LevelPoint> ReplayPoints(
        IReadOnlyList<Candle> history,
        IReadOnlyList<Candle>? subHistory,
        string? subInterval,
        ChanOptions options,
        long fromTime,
        long toTime)
    {
        var results = new List<LevelPoint>();
        if (history.Count == 0) return results;

        var windowBars = Math.Max(120, options.AnalysisBars);
        const int subWindowBars = 300;

        for (var i = 0; i < history.Count; i++)
        {
            var barTime = history[i].Time;
            if (barTime < fromTime || barTime > toTime) continue;
            if (i + 1 < windowBars) continue; // 历史不足以构成完整内部窗口

            var window = new Candle[windowBars];
            for (var k = 0; k < windowBars; k++) window[k] = history[i - windowBars + 1 + k];

            var closes = new decimal[windowBars];
            var highs = new decimal[windowBars];
            var lows = new decimal[windowBars];
            for (var k = 0; k < windowBars; k++)
            {
                closes[k] = window[k].Close;
                highs[k] = window[k].High;
                lows[k] = window[k].Low;
            }

            var macdHist = Macd.Compute(closes).Hist;
            var atr = Atr.Compute(highs, lows, closes, 14);
            // 联合打分特征：与在线同口径，在"缠论内部窗口"上计算
            var ema20 = Ema.Compute(closes, 20);
            var ema50 = Ema.Compute(closes, 50);
            var ema200 = Ema.Compute(closes, 200);
            var rsi = Rsi.Compute(closes, 14);

            Candle[]? subWindow = null;
            if (subHistory is { Count: > 0 } && !string.IsNullOrEmpty(subInterval))
            {
                var upto = subHistory.Where(c => c.Time <= barTime).ToArray();
                if (upto.Length > 60)
                    subWindow = upto.Length > subWindowBars ? upto[^subWindowBars..] : upto;
            }

            var chan = ChanAnalyzer.Analyze(window, macdHist, atr, options, subWindow, subInterval);

            // 只在"记账时间 == 当前K线"时产出（与在线一致：信号在该K线收盘时可知）
            foreach (var point in chan.Points.Where(p => p.Time == barTime))
            {
                var dmi = AdxDmi.Compute(highs, lows, closes, 14);
                var boll = BollingerBands.Compute(closes, 20, 2.0m);
                var last = windowBars - 1;
                var adx = (dmi.Adx[last]) is not null ? dmi.Adx[last] : (decimal?)null;
                var atrPct = (atr[last]) is not null && closes[last] > 0 ? atr[last] / closes[last] : (decimal?)null;
                var mid = boll.Middle[last];
                var bandwidth = (mid) is not null && mid > 0
                    ? (boll.Upper[last] - boll.Lower[last]) / mid
                    : (decimal?)null;
                var detail = JointScoreRules.Compute(
                    point.Price, ema200[last],
                    rsi.Length > last ? rsi[last] : null,
                    macdHist.Length > last ? macdHist[last] : null,
                    point.Side, point.Kind, point.AreaRatio,
                    JointScoreRules.RiskPct(point.Price, point.StopPrice),
                    JointScoreRules.LagShare(point.Price, point.ReferencePrice, point.StopPrice));
                results.Add(new LevelPoint(point, adx, atrPct, bandwidth, detail));
            }
        }

        return results;
    }

    private static bool? TrendAligned(IReadOnlyList<Candle>? htf, decimal?[]? ema50, long htfSeconds, long time,
        string side)
    {
        if (htf is null || ema50 is null || htfSeconds <= 0) return null;
        for (var j = htf.Count - 1; j >= 0; j--)
        {
            if (htf[j].Time + htfSeconds > time) continue;
            if ((ema50[j]) is null) return null;
            var above = htf[j].Close > ema50[j];
            return side == "buy" ? above : !above;
        }

        return null;
    }
}
