using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>
/// 结构位置（确定性输出）测试：中枢归属（上/内/下）、笔内回撤、结构失效位距离、多级别一致性。
/// 所有断言都可由输入直接复算——同一输入任何时候必须得到同一结果。
/// </summary>
public static class StructurePositionTests
{
    public static void Register(TestKit t)
    {
        t.Case("结构位置_价格在中枢内_判定为震荡", () =>
        {
            var r = Compute(lastPrice: 100m, zg: 105m, zd: 95m);
            Assert.Equal("in", r.PivotZone);
            Assert.Equal(true, r.EdgeDistancePct is null);   // 中枢内不谈"距边界"
        });

        t.Case("结构位置_价格在中枢上方_给出距离与ATR倍数", () =>
        {
            var r = Compute(lastPrice: 110m, zg: 100m, zd: 90m, atr: 5m);
            Assert.Equal("above", r.PivotZone);
            Assert.Equal((110m - 100m) / 110m, r.EdgeDistancePct!.Value, 8);
            Assert.Equal(10m / 5m, r.EdgeDistanceAtr!.Value, 6);   // 距上沿 10 → 2×ATR
        });

        t.Case("结构位置_价格在中枢下方_镜像", () =>
        {
            var r = Compute(lastPrice: 80m, zg: 100m, zd: 90m, atr: 2m);
            Assert.Equal("below", r.PivotZone);
            Assert.Equal((90m - 80m) / 80m, r.EdgeDistancePct!.Value, 8);
            Assert.Equal(10m / 2m, r.EdgeDistanceAtr!.Value, 6);
        });

        t.Case("结构位置_上升笔回撤_极值端为0_回到起点为1", () =>
        {
            var atEnd = Compute(lastPrice: 120m, zg: null, zd: null, strokeFrom: 100m, strokeTo: 120m, isUp: true);
            Assert.Equal(0m, atEnd.RetracePct!.Value, 6);
            var atStart = Compute(lastPrice: 100m, zg: null, zd: null, strokeFrom: 100m, strokeTo: 120m, isUp: true);
            Assert.Equal(1m, atStart.RetracePct!.Value, 6);
            var mid = Compute(lastPrice: 110m, zg: null, zd: null, strokeFrom: 100m, strokeTo: 120m, isUp: true);
            Assert.Equal(0.5m, mid.RetracePct!.Value, 6);
        });

        t.Case("结构位置_下降笔回撤_方向镜像", () =>
        {
            var mid = Compute(lastPrice: 90m, zg: null, zd: null, strokeFrom: 100m, strokeTo: 80m, isUp: false);
            Assert.Equal(0.5m, mid.RetracePct!.Value, 6);
        });

        t.Case("结构位置_越过端点_回撤为负表示延伸", () =>
        {
            var r = Compute(lastPrice: 126m, zg: null, zd: null, strokeFrom: 100m, strokeTo: 120m, isUp: true);
            Assert.Equal(-0.3m, r.RetracePct!.Value, 6);   // (120−126)/20
            Assert.Contains("结构在延伸", r.Summary);
        });

        t.Case("结构位置_全幅回撤_提示笔或被破坏", () =>
        {
            // 下降笔 100→80，现价 105：已越过起点 5 个点（全幅回撤 125%）
            var r = Compute(lastPrice: 105m, zg: null, zd: null, strokeFrom: 100m, strokeTo: 80m, isUp: false);
            Assert.Equal(1.25m, r.RetracePct!.Value, 6);
            Assert.Contains("全幅回撤", r.Summary);
            Assert.Contains("或将被破坏", r.Summary);
        });

        t.Case("结构位置_无中枢时不给边界距离", () =>
        {
            var r = Compute(lastPrice: 100m, zg: null, zd: null);
            Assert.Equal("none", r.PivotZone);
            Assert.Equal(true, r.EdgeDistancePct is null && r.EdgeDistanceAtr is null);
        });

        t.Case("结构位置_结构失效位距离_等于1R口径", () =>
        {
            var r = Compute(lastPrice: 100m, zg: 105m, zd: 95m, lastKind: "3买", stopPrice: 98m, barsSince: 3);
            Assert.Equal(0.02m, r.DistanceToInvalidationPct!.Value, 8);
            Assert.Equal("3买", r.LastKind);
            Assert.Equal(3, r.BarsSinceLastSignal);
        });

        t.Case("结构位置_多级别归属一致与分歧", () =>
        {
            var aligned = Compute(lastPrice: 110m, zg: 100m, zd: 90m, levels:
            [
                new LevelPosition("higher", "4h", "above", 0.05m, 100m, 90m, 5),
                new LevelPosition("primary", "1h", "above", 0.09m, 100m, 90m, 3),
            ]);
            Assert.Equal("aligned", aligned.CrossLevel);
            Assert.Contains("各级别中枢归属一致", aligned.Summary);

            var mixed = Compute(lastPrice: 95m, zg: 100m, zd: 90m, levels:
            [
                new LevelPosition("higher", "4h", "above", 0.05m, 100m, 90m, 5),
                new LevelPosition("primary", "1h", "in", null, 100m, 90m, 3),
            ]);
            Assert.Equal("mixed", mixed.CrossLevel);
            Assert.Contains("分歧", mixed.Summary);
        });

        t.Case("结构位置_确定性_同一输入重复计算完全一致", () =>
        {
            var a = Compute(lastPrice: 103m, zg: 105m, zd: 95m, strokeFrom: 90m, strokeTo: 104m, isUp: true, atr: 2m);
            var b = Compute(lastPrice: 103m, zg: 105m, zd: 95m, strokeFrom: 90m, strokeTo: 104m, isUp: true, atr: 2m);
            Assert.Equal(a.Summary, b.Summary);
            Assert.Equal(a.RetracePct!.Value, b.RetracePct!.Value, 10);
            Assert.Equal(a.PivotZone, b.PivotZone);
        });
    }

    /// <summary>构造最小场景：n 根K线 + 可选的一笔/一个中枢（端点由参数直接给出，便于独立复算）。</summary>
    private static StructurePosition Compute(
        decimal lastPrice, decimal? zg, decimal? zd,
        decimal? strokeFrom = null, decimal? strokeTo = null, bool isUp = true,
        decimal? atr = null, string? lastKind = null, decimal? stopPrice = null, int barsSince = 0,
        IReadOnlyList<LevelPosition>? levels = null)
    {
        var candles = Enumerable.Range(0, 10)
            .Select(i => new Candle(1_000_000 + i * 3600L, lastPrice, lastPrice, lastPrice, lastPrice, 1m))
            .ToList();
        var strokes = new List<ChanStroke>();
        if (strokeFrom is { } from && strokeTo is { } to)
        {
            strokes.Add(new ChanStroke(
                StartBarIndex: 4, StartPrice: from, EndBarIndex: 8, EndPrice: to,
                IsUp: isUp, IsConfirmed: true, StableFromBarIndex: 8));
        }
        var pivots = new List<ChanPivot>();
        if (zg is { } g && zd is { } d)
        {
            pivots.Add(new ChanPivot(
                Zg: g, Zd: d, StartStrokeIndex: 0, EndStrokeIndex: 0, StrokeCount: 3,
                IsConfirmed: true, LeavingStrokeIndex: null));
        }
        var points = new List<ChanBuySellPoint>();
        if (lastKind is not null)
        {
            points.Add(new ChanBuySellPoint(
                Kind: lastKind, Side: lastKind.EndsWith('买') ? "buy" : "sell",
                Time: candles[candles.Count - 1 - barsSince].Time, Price: lastPrice * 0.98m,
                ReferencePrice: lastPrice * 0.97m, ReferenceBarIndex: 6,
                StopPrice: stopPrice ?? 0m, AreaRatio: null, Note: ""));
        }
        var result = new ChanResult([], strokes, pivots, points, new Dictionary<string, decimal?[]>());
        var atrArr = Enumerable.Range(0, candles.Count).Select(_ => atr).ToArray();
        return StructurePositionCalculator.Compute(candles, result, atrArr, levels ?? []);
    }
}
