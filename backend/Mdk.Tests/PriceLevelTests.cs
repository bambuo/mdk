using Mdk.Api.Analysis;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>
/// 支撑/阻力位点（口径 v2）：固定内部窗口（可复现）、加权强度（反应 × 时间衰减）、
/// 客观锚点（前日/前周高低、整数关口、日 VWAP）。
///
/// 位点不产生信号、不进台账，"强度分"是展示用启发式——故此处只锁**口径**
/// （可复现、可解释、不含未来数据），不宣称预测力。
/// </summary>
public static class PriceLevelTests
{
    public static void Register(TestKit t)
    {
        t.Case("位点_内部窗口固定_不随显示窗口变化", () =>
        {
            var all = Series(400, i => 100m + i % 20);
            var small = AnalysisWindows.Select(all, 60, 200, 300);
            var large = AnalysisWindows.Select(all, 300, 200, 300);

            Assert.NotEqual(small.Display.Count, large.Display.Count);   // 显示窗口随调用方
            Assert.Equal(small.Chan.Count, large.Chan.Count);            // 内部窗口固定
            Assert.Equal(small.Levels.Count, large.Levels.Count);
            Assert.Equal(small.Chan[0].Time, large.Chan[0].Time);
            Assert.Equal(small.Levels[0].Time, large.Levels[0].Time);
        });

        t.Case("位点_反应强且新近的触碰得分更高", () =>
        {
            var bars = new List<decimal>();
            bars.AddRange(Repeat(92m, Warmup));          // 预热：让 ATR 有值
            bars.AddRange(Repeat(92m, 4));
            bars.Add(90m);                               // 旧的低点：仅反弹到 91（弱反应）
            bars.AddRange(Repeat(91m, 124));             // 横盘远离该位点，制造时间衰减
            bars.AddRange([88m, 84m, 80m]);              // 新近的低点：随后强反弹
            bars.AddRange([84m, 88m, 92m, 96m]);
            bars.AddRange(Repeat(96m, 10));

            var levels = PriceLevels.Analyze(Build(bars), new PriceLevelOptions { Anchors = false });
            var strong = levels.First(l => Math.Abs(l.Price - 80m) < 1.5m);
            var weak = levels.First(l => Math.Abs(l.Price - 90m) < 1.5m);

            Assert.True(strong.Score > weak.Score,
                $"新近且强反弹的位点应得分更高（80 → {strong.Score}，90 → {weak.Score}）");
            Assert.True(strong.ReactionAtr > weak.ReactionAtr, "80 处的反应幅度应更大");
            Assert.True(weak.AgeBars > strong.AgeBars, "90 处的触碰应更久远");
        });

        t.Case("位点_整数关口_上下各一个_未测试故得分为零", () =>
        {
            // 现价 83,172 → 步长 1,000：上方 84,000、下方 83,000；横盘不产生摆动极值
            var levels = PriceLevels.Analyze(Series(80, _ => 83_172m), new PriceLevelOptions { MaxPerSide = 10 });
            var round = levels.Where(l => l.Sources.Contains("整数关口")).ToList();

            Assert.True(round.Count >= 2, $"应有上下的整数关口（实际 {round.Count}）");
            Assert.Contains(round, l => l.Price == 84_000m);
            Assert.Contains(round, l => l.Price == 83_000m);
            Assert.All(round, l => Assert.Equal(0m, l.Score));       // 锚点未经触碰：强度分为 0
            Assert.All(round, l => Assert.Equal(0, l.Touches));
        });

        t.Case("位点_前日高低_按UTC日切分且只取已收盘的那一段", () =>
        {
            const long day = 86_400L;
            var dayStart = 1_700_000_000L / day * day;   // 某个 UTC 00:00
            var bars = new List<decimal>();

            for (var i = 0; i < 24; i++) bars.Add(103m);  // 前一日
            bars[10] = 110m;                              // 前日高
            bars[5] = 100m;                               // 前日低
            for (var i = 0; i < 24; i++) bars.Add(95m);   // 当日（其极值不得混入"前日"）
            bars[24 + 3] = 96m;
            bars[24 + 8] = 90m;

            var candles = bars.Select((m, i) => new Candle(dayStart - day + i * 3600L, m, m + 0.5m, m - 0.5m, m, 10)).ToList();
            var levels = PriceLevels.Analyze(candles, new PriceLevelOptions { MaxPerSide = 10 });
            var prevHigh = levels.FirstOrDefault(l => l.Sources.Contains("前日高"));
            var prevLow = levels.FirstOrDefault(l => l.Sources.Contains("前日低"));

            // 前日高的价 = 那根K线的最高价（mid 110 + 0.5 影线），前日低同理
            Assert.True(prevHigh is not null && prevLow is not null, "应识别前日高/前日低");
            Assert.Equal(110.5m, prevHigh!.Price);
            Assert.Equal(99.5m, prevLow!.Price);
        });

        t.Case("位点_日VWAP_按成交量加权且只含当日", () =>
        {
            const long day = 86_400L;
            var dayStart = 1_700_000_000L / day * day;
            var candles = new List<Candle>
            {
                // 前一日：极端价 200，不得进入当日 VWAP
                new(dayStart - 3600, 200m, 200m, 200m, 200m, 1_000),
            };
            for (var i = 0; i < 8; i++) candles.Add(new Candle(dayStart + i * 3600, 100m, 100m, 100m, 100m, 1));
            for (var i = 8; i < 10; i++) candles.Add(new Candle(dayStart + i * 3600, 110m, 110m, 110m, 110m, 1));

            var levels = PriceLevels.Analyze(candles, new PriceLevelOptions { MaxPerSide = 10 });
            var vwap = levels.FirstOrDefault(l => l.Sources.Contains("日VWAP"));

            Assert.True(vwap is not null, "应输出日 VWAP 位点");
            Assert.Equal(102m, vwap!.Price, 4);            // (8×100 + 2×110) ÷ 10
        });

        t.Case("位点_容差按ATR_超出容差各自成点位", () =>
        {
            var bars = new List<decimal>();
            bars.AddRange(Repeat(100m, Warmup));
            bars.AddRange(Repeat(100m, 4));
            bars.Add(96m);                                 // 低点 A
            bars.AddRange(Repeat(100m, 4));
            bars.Add(90m);                                 // 低点 B（与 A 相距 6，远超 0.5×ATR）
            bars.AddRange(Repeat(100m, 6));

            var levels = PriceLevels.Analyze(Build(bars),
                new PriceLevelOptions { Anchors = false, ClusterToleranceAtr = 0.5m });
            var a = levels.FirstOrDefault(l => Math.Abs(l.Price - 96m) < 1.5m);
            var b = levels.FirstOrDefault(l => Math.Abs(l.Price - 90m) < 1.5m);

            Assert.True(a is not null && b is not null, "超出容差的两个极值应各自成为位点");
            Assert.Equal(1, a!.Touches);
            Assert.Equal(1, b!.Touches);
        });

        t.Case("位点_锚点与摆动点重合_合并为一个位点并标注两个来源", () =>
        {
            var bars = new List<decimal>();
            bars.AddRange(Repeat(1040m, Warmup));
            bars.AddRange(Repeat(1040m, 5));
            bars.Add(1000m);                               // 摆动低点，恰在整数关口 1000 上
            bars.AddRange(Repeat(1040m, 5));

            var levels = PriceLevels.Analyze(Build(bars), new PriceLevelOptions { Anchors = true });
            var merged = levels.FirstOrDefault(l => l.Sources.Contains("摆动点") && l.Sources.Contains("整数关口"));
            var pure = levels.Where(l => l.Sources.Count == 1 && l.Sources[0] == "整数关口").ToList();

            Assert.True(merged is not null, "重合的锚点应并入摆动点位点并标注两个来源");
            Assert.Equal(1, merged!.Touches);              // 锚点重合不加触碰次数
            Assert.True(merged.Score > 0, "合并后仍保留摆动点的强度分");
            Assert.Contains(pure, l => l.Price == 1100m);  // 上方整数关口仍独立存在
        });

        t.Case("位点_分侧与限量_各取最近MaxPerSide个且按距离排序", () =>
        {
            // 上方三个极值（105/110/115）、下方三个极值（95/90/85），末根收在 100
            var bars = new List<decimal>();
            bars.AddRange(Repeat(100m, Warmup));
            bars.AddRange([101m, 103m, 105m, 103m, 101m]);
            bars.AddRange([101m, 105m, 110m, 105m, 101m]);
            bars.AddRange([101m, 106m, 111m, 115m, 111m, 106m, 101m]);
            bars.AddRange([100m, 97m, 95m, 97m, 100m]);
            bars.AddRange([100m, 95m, 90m, 95m, 100m]);
            bars.AddRange([100m, 92m, 85m, 92m, 100m]);
            bars.AddRange(Repeat(100m, 4));

            var options = new PriceLevelOptions { Anchors = false, MaxPerSide = 2 };
            var levels = PriceLevels.Analyze(Build(bars), options);
            var supports = levels.Where(l => l.Kind == "support").ToList();
            var resistances = levels.Where(l => l.Kind == "resistance").ToList();
            var last = bars[^1];

            Assert.True(supports.Count <= 2 && resistances.Count <= 2, "每侧不得超过 MaxPerSide");
            Assert.All(supports, l => Assert.True(l.Price < last, "支撑必须低于现价"));
            Assert.All(resistances, l => Assert.True(l.Price >= last, "阻力必须不低于现价"));
            for (var i = 1; i < supports.Count; i++)
                Assert.True(Math.Abs(supports[i].DistancePct) >= Math.Abs(supports[i - 1].DistancePct), "支撑应按由近及远排序");
            for (var i = 1; i < resistances.Count; i++)
                Assert.True(resistances[i].DistancePct >= resistances[i - 1].DistancePct, "阻力应按由近及远排序");
        });
    }

    /// <summary>预热根数：ATR(14) 需要足够根数才有值，预热段为横盘（不产生摆动极值）。</summary>
    private const int Warmup = 20;

    private static List<Candle> Build(List<decimal> mids)
    {
        var time = 1_700_000_000L;
        return mids.Select((m, i) => new Candle(time + i * 3600L, m, m + 0.5m, m - 0.5m, m, 10)).ToList();
    }

    /// <summary>横盘序列（无摆动极值），用于锚点类用例。</summary>
    private static List<Candle> Series(int count, Func<int, decimal> mid) => Build([.. Enumerable.Range(0, count).Select(mid)]);

    private static IEnumerable<decimal> Repeat(decimal value, int count) => Enumerable.Repeat(value, count);
}
