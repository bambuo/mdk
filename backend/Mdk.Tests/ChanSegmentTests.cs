using Mdk.Api.Analysis.Chan;

namespace Mdk.Tests;

/// <summary>
/// 线段构建（特征序列法）：
/// 延伸不破坏 / 第一种破坏（无缺口）/ 第二种破坏（有缺口且回补）/ 有缺口未回补 /
/// 最小三笔约束 / 首尾相连与方向交替 / 模式切换回归。
/// </summary>
public static class ChanSegmentTests
{
    /// <summary>构造笔：给定 (起点价, 终点价) 序列，K线索引按每笔跨 2 根递增。</summary>
    private static List<ChanStroke> MakeStrokes(params double[] prices)
    {
        var strokes = new List<ChanStroke>();
        for (var i = 0; i + 1 < prices.Length; i++)
        {
            var from = prices[i];
            var to = prices[i + 1];
            strokes.Add(new ChanStroke(
                StartBarIndex: i * 2,
                StartPrice: from,
                EndBarIndex: i * 2 + 2,
                EndPrice: to,
                IsUp: to > from,
                IsConfirmed: true,
                StableFromBarIndex: i * 2 + 4));
        }
        return strokes;
    }

    public static void Register(TestKit t)
    {
        t.Case("线段_持续创新高不破坏_形成未确认线段", () =>
        {
            // 上升线段内每个回调低点不断抬高：特征序列元素依次抬高 → 无顶分型 → 线段延伸
            var strokes = MakeStrokes(0, 10, 6, 16, 12, 22, 18, 28);
            var segments = ChanSegmentBuilder.Build(strokes);

            Assert.Equal(1, segments.Count);
            Assert.Equal(false, segments[0].IsConfirmed);
            Assert.Equal(true, segments[0].IsUp);
            Assert.Equal(7, segments[0].StrokeCount);   // 8 个价位 → 7 笔，全部属于同一未确认线段
            Assert.Equal(28, segments[0].EndPrice, 6);  // 终点随延伸推进到最新高点
        });

        t.Case("线段_第一种破坏_特征序列无缺口时立即确认", () =>
        {
            // s0↑0→10, s1↓10→6, s2↑6→14, s3↓14→7（更深的回调=分型中元素）, s4↑7→12（未创新高）, s5↓12→5
            // 特征序列 e1=[6,10], e2=[7,14], e3=[5,12] → 顶分型且无缺口（e1.High=10 ≥ e2.Low=7）
            var strokes = MakeStrokes(0, 10, 6, 14, 7, 12, 5);
            var segments = ChanSegmentBuilder.Build(strokes);

            var first = segments[0];
            Assert.Equal(true, first.IsConfirmed);
            Assert.Equal(true, first.IsUp);
            Assert.Equal(3, first.StrokeCount);            // 0..2 笔（终点为峰值那笔）
            Assert.Equal(14, first.EndPrice, 6);           // 线段终点 = 峰值
            Assert.True(first.StableFromBarIndex is not null, "确认线段必须有稳定时点");
        });

        t.Case("线段_第二种破坏_有缺口但被后续回补后确认", () =>
        {
            // s0↑0→10, s1↓10→6, s2↑6→16, s3↓16→13, s4↑13→15, s5↓15→12, s6↑12→14, s7↓14→9
            // 特征序列 e1=[6,10], e2=[13,16]（e2.Low=13 > e1.High=10 → 缺口）, e3=[12,15] → 带缺口顶分型
            // 后续 e4=[9,14]：低点 9 ≤ e1.High=10 → 回补缺口 → 确认破坏
            var strokes = MakeStrokes(0, 10, 6, 16, 13, 15, 12, 14, 9);
            var segments = ChanSegmentBuilder.Build(strokes);

            var first = segments[0];
            Assert.Equal(true, first.IsConfirmed);
            Assert.Equal(16, first.EndPrice, 6);                      // 线段终点为该段最高点
            Assert.Equal(2, first.EndStrokeIndex);
            Assert.Equal(strokes[7].EndBarIndex, first.StableFromBarIndex!.Value);  // 确认时点 = 回补缺口那根K线
            Assert.Equal(3, first.StrokeCount);
        });

        t.Case("线段_第二种破坏_缺口未回补则线段不结束", () =>
        {
            // 与上一例同构，但最后一笔只回到 11（未回补缺口：需低点 ≤ 10）
            var strokes = MakeStrokes(0, 10, 6, 16, 13, 15, 12, 14, 11);
            var segments = ChanSegmentBuilder.Build(strokes);

            Assert.Equal(1, segments.Count);
            Assert.Equal(false, segments[0].IsConfirmed, "缺口未回补时不应确认破坏");
        });

        t.Case("线段_最小三笔_已确认线段笔数不低于 3", () =>
        {
            var strokes = MakeStrokes(0, 10, 6, 14, 7, 12, 5, 16, 2, 20, 1, 25, 3);
            var segments = ChanSegmentBuilder.Build(strokes);
            Assert.True(segments.Count > 0, "应至少产出一段");
            Assert.All(segments.Where(s => s.IsConfirmed).ToList(),
                s => Assert.True(s.StrokeCount >= ChanSegmentBuilder.MinStrokes,
                    $"已确认线段笔数不足 3：{s.StrokeCount}"));
        });

        t.Case("线段_首尾相连且方向交替", () =>
        {
            var strokes = MakeStrokes(0, 10, 6, 14, 7, 12, 5, 18, 2, 13, 6, 22, 4, 16, 8, 24, 5);
            var segments = ChanSegmentBuilder.Build(strokes);
            Assert.True(segments.Count >= 2, $"应至少两段（实际 {segments.Count}）");

            for (var i = 1; i < segments.Count; i++)
            {
                Assert.Equal(segments[i - 1].EndBarIndex, segments[i].StartBarIndex);   // 首尾相连
                Assert.True(segments[i - 1].IsUp != segments[i].IsUp, "方向必须交替");
                Assert.True(segments[i].StartStrokeIndex > segments[i - 1].StartStrokeIndex, "笔序号应递增");
            }
        });

        t.Case("线段_不足三笔时不产出", () =>
        {
            var strokes = MakeStrokes(0, 10, 5);   // 仅两笔
            Assert.Equal(0, ChanSegmentBuilder.Build(strokes).Count);
        });

        t.Case("线段模式_开关关闭时保持笔中枢机制不变", () =>
        {
            var candles = SyntheticCandles();
            var macd = Mdk.Api.Indicators.Macd.Compute(candles.Select(c => c.Close).ToArray()).Hist;
            var atr = Mdk.Api.Indicators.Atr.Compute(
                candles.Select(c => c.High).ToArray(), candles.Select(c => c.Low).ToArray(),
                candles.Select(c => c.Close).ToArray(), 14);

            var strokeMode = ChanAnalyzer.Analyze(candles, macd, atr, new ChanOptions { AnalysisBars = 200, RequireSubLevelConfirm = false, UseSegments = false });
            var segmentMode = ChanAnalyzer.Analyze(candles, macd, atr, new ChanOptions { AnalysisBars = 200, RequireSubLevelConfirm = false, UseSegments = true });

            Assert.Equal("stroke", strokeMode.LevelMode);
            Assert.Equal("segment", segmentMode.LevelMode);
            Assert.True((strokeMode.Segments ?? []).Count == 0, "笔模式下不应有线段");
            Assert.True((segmentMode.Segments ?? []).Count > 0, "线段模式下应构建出线段");
            Assert.Equal(false, segmentMode.Series.ContainsKey("chanSegment") == false, "线段模式应输出线段序列");
            Assert.True(segmentMode.Pivots.Count <= strokeMode.Pivots.Count,
                $"线段中枢应不粗于笔中枢（线段 {segmentMode.Pivots.Count} vs 笔 {strokeMode.Pivots.Count}）");
        });
    }

    private static List<Mdk.Api.Models.Candle> SyntheticCandles()
    {
        var candles = new List<Mdk.Api.Models.Candle>();
        var time = 7_000_000L;
        for (var i = 0; i < 240; i++)
        {
            var price = 100 + Math.Sin(i / 5.0) * 8 + Math.Sin(i / 19.0) * 4;
            candles.Add(new Mdk.Api.Models.Candle(time + i * 3600L, price, price + 1.0, price - 1.0, price, 10));
        }
        return candles;
    }
}
