using Mdk.Api.Analysis;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>
/// 位点检验器的机制自检：事件采集、波次去重、距离分层比较。
/// 结论是"没有优势"——正因如此，机制本身必须被锁住：机制错了，负结论同样不可信。
/// </summary>
public static class LevelHoldStudyTests
{
    public static void Register(TestKit t)
    {
        t.Case("位点检验_合成震荡行情能采到事件_且真实与对照都有", () =>
        {
            var candles = Wave(400);
            var options = new LevelHoldStudyOptions { AnalysisBars = 120, TouchWindow = 60, HoldWindow = 20 };
            var events = LevelHoldStudy.Collect("TESTUSDT", "1h", candles, options);

            Assert.True(events.Count > 0, "应采集到位点事件");
            Assert.True(events.Any(e => !e.IsControl), "应有真实位点事件");
            Assert.True(events.Any(e => e.IsControl), "应有对照事件（否则检验无从比较）");
            Assert.All(events, e => Assert.True(
                e.Outcome is "held" or "broke" or "pending" or "noTouch", $"结局取值非法：{e.Outcome}"));
            // 对照必须避开位点：与同侧真实事件的距离不应完全相同
            Assert.All(events.Where(e => e.IsControl), e => Assert.True(e.DistanceAtr > 0, "对照距离应为正"));
        });

        t.Case("位点检验_波次去重_同方向24根内聚为一波", () =>
        {
            // 手工构造三个同标的同周期同方向、每根间隔 1 小时的事件（24 根内）→ 只算一波
            var events = new List<LevelHoldEvent>
            {
                Event(time: 1_700_000_000L, outcome: "held"),
                Event(time: 1_700_000_000L + 3600, outcome: "held"),
                Event(time: 1_700_000_000L + 7200, outcome: "broke"),
            };

            var result = LevelHoldStudy.Summarize(events,
                new LevelHoldStudyOptions(),
                new Dictionary<string, long> { ["1h"] = 3600 });

            var all = result.Groups[0];
            Assert.Equal(3, all.Events);
            Assert.Equal(1, all.Episodes);                 // 三个事件 = 一波（取首个代表）
            Assert.Equal(1, all.Held);
            Assert.Equal(0, all.Broke);
        });

        t.Case("位点检验_距离分层_真实与对照同水平时优势接近零", () =>
        {
            // 同距离档位内：真实 6 成守住、对照 6 成守住 → 优势应≈0（且不通过 +10% 判据）
            var events = new List<LevelHoldEvent>();
            for (var i = 0; i < 60; i++)
            {
                var time = 1_700_000_000L + i * 3600L * 30;     // 间隔 30 根 = 独立波次
                var realHeld = i % 10 < 6;
                events.Add(Event(time, realHeld ? "held" : "broke", control: false));
                var controlHeld = (i + 5) % 10 < 6;
                events.Add(Event(time, controlHeld ? "held" : "broke", control: true));
            }

            var result = LevelHoldStudy.Summarize(events,
                new LevelHoldStudyOptions(),
                new Dictionary<string, long> { ["1h"] = 3600 });

            Assert.True(result.Buckets > 0, "应有可比较的距离档位");
            Assert.True(Math.Abs(result.StratifiedEdge) < 0.15m,
                $"同水平的真实与对照，分层优势应接近 0（实际 {result.StratifiedEdge:P1}）");
            Assert.False(result.Passed, "优势未达 +10% 时不得判为通过");
            Assert.True(result.Reasons.Count > 0, "未通过必须给出理由");
        });
    }

    private static LevelHoldEvent Event(long time, string outcome, bool control = false, decimal distanceAtr = 1m) =>
        new("TESTUSDT", "1h", "support", control, control ? "对照" : "摆动点", 3m,
            time, 100m, 101m, distanceAtr, outcome, 5);

    /// <summary>合成震荡序列：正弦式上下摆动，便于形成摆动极值与位点。</summary>
    private static List<Candle> Wave(int count)
    {
        var candles = new List<Candle>(count);
        for (var i = 0; i < count; i++)
        {
            var mid = 100m + (decimal)Math.Sin(i / 7.0) * 4m;
            candles.Add(new Candle(1_700_000_000L + i * 3600L, mid, mid + 0.3m, mid - 0.3m, mid, 10m, 4m));
        }

        return candles;
    }
}
