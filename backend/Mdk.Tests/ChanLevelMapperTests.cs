using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>
/// 多级别结构映射：时间窗口过滤、数量上限、以及"级别归属"两个口径的判据。
/// </summary>
public static class ChanLevelMapperTests
{
    private static readonly long T0 = 1_000_000;

    private static Candle Bar(int index) => new(T0 + index * 3600L, 100, 100.5, 99.5, 100, 10);

    private static ChanStroke Stroke(int startBar, double startPrice, int endBar, double endPrice) =>
        new(startBar, startPrice, endBar, endPrice, endPrice > startPrice, IsConfirmed: true, StableFromBarIndex: endBar + 2);

    /// <summary>构造一个含 n 个中枢的结果：第 k 个中枢由 3 笔构成，价格区间 [100+k, 104+k]，时间 [k*4, k*4+4]。</summary>
    private static (IReadOnlyList<Candle> Candles, ChanResult Result) BuildLevel(int pivotCount, int barReuse = 6)
    {
        var candles = Enumerable.Range(0, (pivotCount + 1) * barReuse + 4).Select(Bar).ToArray();
        var strokes = new List<ChanStroke>();
        var pivots = new List<ChanPivot>();
        for (var k = 0; k <= pivotCount; k++)
        {
            var baseBar = k * barReuse;
            var low = 100.0 + k;
            var high = 104.0 + k;
            strokes.Add(Stroke(baseBar, low, baseBar + 2, high));
            strokes.Add(Stroke(baseBar + 2, high, baseBar + 3, low + 0.5));
            strokes.Add(Stroke(baseBar + 3, low + 0.5, baseBar + 4, high - 0.5));
            pivots.Add(new ChanPivot(
                Zg: high - 0.5, Zd: low + 0.5,
                StartStrokeIndex: k * 3, EndStrokeIndex: k * 3 + 2, StrokeCount: 3,
                IsConfirmed: true, LeavingStrokeIndex: k * 3 + 3 < strokes.Count + 3 ? null : null));
        }
        var result = new ChanResult([], strokes, pivots, [],
            new Dictionary<string, double?[]>());
        return (candles, result);
    }

    public static void Register(TestKit t)
    {
        t.Case("级别映射_只保留与显示窗口相交的中枢", () =>
        {
            var (candles, result) = BuildLevel(pivotCount: 5);
            // 窗口取中间一段（第 2 个中枢之后）
            var from = candles[12].Time;
            var to = candles[^1].Time;
            var infos = ChanLevelMapper.ToPivotInfos(candles, result, from, to, maxPivots: 10);

            Assert.True(infos.Count > 0, "窗口内应有中枢");
            Assert.All(infos, p => Assert.True(p.ToTime >= from && p.FromTime <= to, "不得包含与窗口无交集的中枢"));
        });

        t.Case("级别映射_超过上限时保留最近的中枢", () =>
        {
            var (candles, result) = BuildLevel(pivotCount: 5);
            var infos = ChanLevelMapper.ToPivotInfos(candles, result, candles[0].Time, candles[^1].Time, maxPivots: 2);
            Assert.Equal(2, infos.Count);
            // 最近的两个：其起始时间应晚于被丢弃者
            var all = ChanLevelMapper.ToPivotInfos(candles, result, candles[0].Time, candles[^1].Time, maxPivots: 10);
            Assert.Equal(all[^1].FromTime, infos[^1].FromTime);
            Assert.Equal(all[^2].FromTime, infos[^2].FromTime);
        });

        t.Case("级别归属_时间与价格都相交才算在高周期中枢内", () =>
        {
            var self = new ChanPivotInfo(2_000, 3_000, Zg: 110, Zd: 105, Strokes: 3, IsConfirmed: true);

            // 高周期中枢：时间相交 + 价格相交 → true
            var overlap = new[] { new ChanPivotInfo(1_000, 2_500, 112, 108, 5, true) };
            Assert.Equal(true, ChanLevelMapper.Annotate(self, overlap, []).InsideHigher);

            // 时间相交但价格不相交（高周期中枢在更上方）→ false
            var priceApart = new[] { new ChanPivotInfo(1_000, 2_500, 130, 128, 5, true) };
            Assert.Equal(false, ChanLevelMapper.Annotate(self, priceApart, []).InsideHigher);

            // 价格相交但时间不相交 → false
            var timeApart = new[] { new ChanPivotInfo(9_000, 9_500, 112, 108, 5, true) };
            Assert.Equal(false, ChanLevelMapper.Annotate(self, timeApart, []).InsideHigher);
        });

        t.Case("级别归属_次级别中枢按“时间中点落在本中枢区间内 + 价格相交”计数", () =>
        {
            var self = new ChanPivotInfo(2_000, 4_000, Zg: 110, Zd: 105, Strokes: 3, IsConfirmed: true);

            var lower = new[]
            {
                new ChanPivotInfo(2_100, 2_900, 109, 106, 3, true),   // 中点在内 + 价格相交 → 计入
                new ChanPivotInfo(3_100, 3_900, 108, 103, 3, true),   // 中点在内 + 价格相交 → 计入
                new ChanPivotInfo(4_200, 4_800, 109, 106, 3, true),   // 中点在外 → 不计
                new ChanPivotInfo(2_200, 2_800, 130, 128, 3, true),   // 中点在内但价格不相交 → 不计
            };
            var (_, count) = ChanLevelMapper.Annotate(self, [], lower);
            Assert.Equal(2, count);
        });

        t.Case("级别映射_数据覆盖起点用于前端提示", () =>
        {
            var candles = new[] { Bar(0), Bar(1), Bar(2) };
            Assert.Equal(candles[0].Time, ChanLevelMapper.CoverageFromTime(candles));
            Assert.Equal(0, ChanLevelMapper.CoverageFromTime([]));
        });

        t.Case("级别映射_中枢数量与结果一致（不凭空多出）", () =>
        {
            var (candles, result) = BuildLevel(pivotCount: 3);
            var infos = ChanLevelMapper.ToPivotInfos(candles, result, candles[0].Time, candles[^1].Time, maxPivots: 100);
            Assert.True(infos.Count <= result.Pivots.Count, "映射出的中枢数不得超过原始结果");
        });
    }
}
