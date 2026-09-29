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
/// · 次级别同样只用"时间 ≤ i 的次级别K线"；
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
        double Price,
        double ReferencePrice,
        double StopPrice,
        double? AreaRatio,
        string Note,
        bool? TrendAligned,
        double? Adx,
        double? AtrPct,
        double? BandwidthPct);

    /// <summary>
    /// 逐根复算。mainHistory 需覆盖 [fromTime - windowBars, toTime]；sub/htf 历史为可选来源。
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
        var results = new List<SeedSignal>();
        if (mainHistory.Count == 0) return results;

        var windowBars = Math.Max(120, options.AnalysisBars);
        var subWindowBars = 300;
        double[]? htfEma50 = null;
        long htfSeconds = 0;
        if (htfHistory is { Count: > 50 } && !string.IsNullOrEmpty(htfInterval))
        {
            htfEma50 = Ema.Compute(htfHistory.Select(c => c.Close).ToArray(), 50);
            htfSeconds = MarketIntervals.IntervalSeconds(htfInterval);
        }

        for (var i = 0; i < mainHistory.Count; i++)
        {
            var barTime = mainHistory[i].Time;
            if (barTime < fromTime || barTime > toTime) continue;
            if (i + 1 < windowBars) continue;   // 历史不足以构成完整内部窗口

            var window = new Candle[windowBars];
            for (var k = 0; k < windowBars; k++) window[k] = mainHistory[i - windowBars + 1 + k];

            var closes = new double[windowBars];
            var highs = new double[windowBars];
            var lows = new double[windowBars];
            for (var k = 0; k < windowBars; k++)
            {
                closes[k] = window[k].Close;
                highs[k] = window[k].High;
                lows[k] = window[k].Low;
            }
            var macdHist = Macd.Compute(closes).Hist;
            var atr = Atr.Compute(highs, lows, closes, 14);

            // 次级别：仅取"时间 ≤ 当前K线"的K线
            Candle[]? subWindow = null;
            if (subHistory is { Count: > 0 } && !string.IsNullOrEmpty(subInterval))
            {
                var upto = subHistory.Where(c => c.Time <= barTime).ToArray();
                if (upto.Length > 60)
                    subWindow = upto.Length > subWindowBars ? upto[^subWindowBars..] : upto;
            }

            var chan = ChanAnalyzer.Analyze(window, macdHist, atr, options, subWindow, subInterval);

            // 只在"记账时间 == 当前K线"时产出（保证与在线一致：信号在该K线收盘时可知）
            foreach (var point in chan.Points.Where(p => p.Time == barTime))
            {
                var dmi = AdxDmi.Compute(highs, lows, closes, 14);
                var boll = BollingerBands.Compute(closes, 20, 2.0);
                var last = windowBars - 1;
                var adx = !double.IsNaN(dmi.Adx[last]) ? dmi.Adx[last] : (double?)null;
                var atrPct = !double.IsNaN(atr[last]) && closes[last] > 0 ? atr[last] / closes[last] : (double?)null;
                var mid = boll.Middle[last];
                var bandwidth = !double.IsNaN(mid) && mid > 0
                    ? (boll.Upper[last] - boll.Lower[last]) / mid
                    : (double?)null;

                results.Add(new SeedSignal(
                    Time: point.Time,
                    Side: point.Side,
                    Kind: point.Kind,
                    Price: point.Price,
                    ReferencePrice: point.ReferencePrice,
                    StopPrice: point.StopPrice,
                    AreaRatio: point.AreaRatio,
                    Note: point.Note,
                    TrendAligned: TrendAligned(htfHistory, htfEma50, htfSeconds, point.Time, point.Side),
                    Adx: adx,
                    AtrPct: atrPct,
                    BandwidthPct: bandwidth));
            }
        }
        return results;
    }

    private static bool? TrendAligned(IReadOnlyList<Candle>? htf, double[]? ema50, long htfSeconds, long time, string side)
    {
        if (htf is null || ema50 is null || htfSeconds <= 0) return null;
        for (var j = htf.Count - 1; j >= 0; j--)
        {
            if (htf[j].Time + htfSeconds > time) continue;
            if (double.IsNaN(ema50[j])) return null;
            var above = htf[j].Close > ema50[j];
            return side == "buy" ? above : !above;
        }
        return null;
    }
}
