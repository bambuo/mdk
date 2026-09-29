using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>
/// 历史回填（ChanBackfill.Replay）测试：
/// ① 与在线口径等价——对某一根K线，回填产出的信号必须与"直接对该K线收盘时的窗口做一次 Analyze"一致；
/// ② 不依赖未来数据——把历史截断在 toTime，回填结果不变。
/// </summary>
public static class ChanBackfillTests
{
    public static void Register(TestKit t)
    {
        t.Case("回填_与在线口径等价（同窗口直接 Analyze 的结果一致）", () =>
        {
            var (main, options) = BuildHistory();
            var fromTime = main[^60].Time;
            var toTime = main[^1].Time;

            var seeded = ChanBackfill.Replay(main, null, null, null, null, options, fromTime, toTime);

            // 对最后一根K线：直接按"截至该K线的内部窗口"跑一次在线同款调用
            var windowBars = options.AnalysisBars;
            var window = main.Skip(main.Count - windowBars).ToArray();
            var closes = window.Select(c => c.Close).ToArray();
            var highs = window.Select(c => c.High).ToArray();
            var lows = window.Select(c => c.Low).ToArray();
            var direct = ChanAnalyzer.Analyze(
                window,
                Mdk.Api.Indicators.Macd.Compute(closes).Hist,
                Mdk.Api.Indicators.Atr.Compute(highs, lows, closes, 14),
                options);
            var directAtLast = direct.Points.Where(p => p.Time == toTime)
                .Select(p => $"{p.Kind}|{p.Side}|{p.Time}|{p.StopPrice:0.########}")
                .OrderBy(x => x).ToList();
            var seededAtLast = seeded.Where(s => s.Time == toTime)
                .Select(s => $"{s.Kind}|{s.Side}|{s.Time}|{s.StopPrice:0.########}")
                .OrderBy(x => x).ToList();

            Assert.True(seeded.Count > 0, $"回填应至少产出 1 个信号（实际 {seeded.Count}）");
            Assert.Equal(directAtLast.Count, seededAtLast.Count);
            Assert.All(seededAtLast, x => Assert.True(directAtLast.Contains(x),
                $"回填信号应逐字段等于在线同款调用的结果：{x}"));
        });

        t.Case("回填_不依赖未来数据（截断历史结果不变）", () =>
        {
            var (main, options) = BuildHistory();
            var toIndex = main.Count - 4;
            var toTime = main[toIndex].Time;
            var fromTime = main[320].Time;

            var truncated = main.Take(toIndex + 1).ToArray();
            var full = ChanBackfill.Replay(main, null, null, null, null, options, fromTime, toTime);
            var cut = ChanBackfill.Replay(truncated, null, null, null, null, options, fromTime, toTime);

            string Key(ChanBackfill.SeedSignal s) => $"{s.Kind}|{s.Side}|{s.Time}|{s.StopPrice:0.########}";
            var fullKeys = full.Select(Key).OrderBy(x => x).ToList();
            var cutKeys = cut.Select(Key).OrderBy(x => x).ToList();

            Assert.True(fullKeys.Count > 0, "合成行情应至少回填出一个信号");
            Assert.Equal(fullKeys.Count, cutKeys.Count);
            Assert.All(fullKeys, x => Assert.True(cutKeys.Contains(x), $"截断历史后缺少回填信号：{x}"));
        });

        t.Case("回填_信号仅在其记账K线那一刻产出一次", () =>
        {
            var (main, options) = BuildHistory();
            var fromTime = main[0].Time;
            var toTime = main[^1].Time;
            var seeded = ChanBackfill.Replay(main, null, null, null, null, options, fromTime, toTime);

            var duplicates = seeded.GroupBy(s => $"{s.Kind}|{s.Side}|{s.Time}")
                .Where(g => g.Count() > 1).ToList();
            Assert.Equal(0, duplicates.Count);
        });
    }

    /// <summary>构造"前置温区 → 通道 → 突破回抽"的合成行情（与界面测试同款几何）。</summary>
    private static (List<Candle> Main, ChanOptions Options) BuildHistory()
    {
        var levels = new List<decimal>();
        void Leg(decimal from, decimal to, int bars)
        {
            for (var k = 1; k <= bars; k++) levels.Add(from + (to - from) * k / bars);
        }
        // 前置温区：14 轮（224 根），保证后续结构落在内部窗口预热之后
        for (var r = 0; r < 14; r++) { Leg(95, 99, 8); Leg(99, 95.5m, 8); }
        for (var r = 0; r < 5; r++) { Leg(100, 104, 10); Leg(104, 100.5m, 10); } // 通道（形成中枢）
        Leg(100.5m, 108, 8);    // 突破
        Leg(108, 105.5m, 8);    // 回抽不回中枢 → 3 买
        Leg(105.5m, 112, 8);
        Leg(112, 107, 8);
        Leg(107, 114, 8);
        Leg(114, 109, 8);
        Leg(109, 111, 8);

        var main = new List<Candle>();
        var time = 6_000_000L;
        for (var i = 0; i < levels.Count; i++)
            main.Add(new Candle(time + i * 3600L, levels[i], levels[i] + 0.5m, levels[i] - 0.5m, levels[i], 10));

        var options = new ChanOptions
        {
            AnalysisBars = 300,               // 比序列短，保证有足够的可回填区间
            WarmupBars = 30,
            RequireSubLevelConfirm = false,   // 回填测试聚焦窗口/时序口径，关闭次级别依赖
        };
        return (main, options);
    }
}
