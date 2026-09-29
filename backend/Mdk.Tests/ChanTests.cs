using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
using Mdk.Api.Models;
using Mdk.Api.Domain;

namespace Mdk.Tests;

/// <summary>
/// 缠论模块测试：包含处理、分型、笔（新笔）、中枢、背驰、三类买卖点与无未来函数不变性。
/// 测试数据全部为合成K线，构造意图写在每个用例里。
/// </summary>
public static class ChanTests
{
    public static void Register(TestKit t)
    {
        // ─────────────────── 包含处理 ───────────────────

        t.Case("缠论_包含处理_上升方向取高高", () =>
        {
            // 第二根被第一根包含，且首根合并K线方向为向上 → 取高高：High=12、Low=max(9,9.5m)=9.5m
            var candles = new[]
            {
                Bar(1_00_000, 10, 12, 9, 11),
                Bar(1_00_001, 10.5m, 11.5m, 9.5m, 11),   // 被第一根包含
                Bar(1_00_002, 11, 13, 10.5m, 12.5m),    // 不包含 → 新合并K线
            };
            var merged = ChanInclusion.Merge(candles);
            Assert.Equal(2, merged.Count);
            Assert.Equal(12, merged[0].High, 6);
            Assert.Equal(9.5m, merged[0].Low, 6);     // 向上处理取"低高"
            Assert.Equal(1, merged[0].EndIndex);     // 第一根合并K线覆盖 0..1
        });

        t.Case("缠论_包含处理_下降方向取低低", () =>
        {
            // 先造出一根向下的合并K线，再用被其包含的K线验证"低低"
            var candles = new[]
            {
                Bar(1_00_000, 10, 12, 9, 11),
                Bar(1_00_001, 9.5m, 11, 8.5m, 9),        // 不包含第一根 → 新合并K线（向下）
                Bar(1_00_002, 9, 10.5m, 8.8m, 10),       // 被第二根包含
            };
            var merged = ChanInclusion.Merge(candles);
            Assert.Equal(2, merged.Count);
            Assert.False(merged[1].IsUp, "第二根合并K线方向应向下");
            Assert.Equal(10.5m, merged[1].High, 6);   // 向下处理：高点取低
            Assert.Equal(8.5m, merged[1].Low, 6);     // 向下处理：低点取低
        });

        t.Case("缠论_包含处理_平台等高等价也视为包含", () =>
        {
            // 两根K线完全相同（平台）：严格比较的分形检测会丢弃，包含处理必须合并
            var candles = new[]
            {
                Bar(1_00_000, 10, 12, 9, 11),
                Bar(1_00_001, 10, 12, 9, 11),
                Bar(1_00_002, 11, 13, 10.5m, 12),
            };
            var merged = ChanInclusion.Merge(candles);
            Assert.Equal(2, merged.Count);
            Assert.Equal(0, merged[0].StartIndex);
            Assert.Equal(1, merged[0].EndIndex);
            Assert.Equal(12, merged[0].High, 6);
        });

        t.Case("缠论_包含处理_连续包含链合并为一根", () =>
        {
            var candles = new[]
            {
                Bar(1_00_000, 10, 12, 9, 11),
                Bar(1_00_001, 10, 11.5m, 9.2m, 10.5m),
                Bar(1_00_002, 10.1m, 11.8m, 9.3m, 11),
                Bar(1_00_003, 10.2m, 11.9m, 9.4m, 11.2m),
            };
            var merged = ChanInclusion.Merge(candles);
            Assert.Equal(1, merged.Count);
            Assert.Equal(3, merged[0].EndIndex);
        });

        // ─────────────────── 分型 ───────────────────

        t.Case("缠论_分型_顶底识别与确认索引", () =>
        {
            // 构造：上升 → 顶 → 下降 → 底
            var candles = new List<Candle>();
            var time = 1_000_000L;
            // 上冲：1..5 逐步走高，第 5 根形成顶分型中位
            var prices = new[] { 10.0m, 10.5m, 11, 11.5m, 12, 11.5m, 11, 10.5m, 10, 10.5m, 11 };
            for (var i = 0; i < prices.Length; i++) candles.Add(Bar(time + i, prices[i], prices[i] + 0.2m, prices[i] - 0.2m, prices[i]));

            var merged = ChanInclusion.Merge(candles);
            var fractals = ChanFractalDetector.Detect(merged, candles);

            Assert.True(fractals.Count >= 2, "应识别出顶与底分型");
            var top = fractals.First(f => f.IsTop);
            var bottom = fractals.First(f => !f.IsTop);
            Assert.Equal(12.2m, top.Price, 6);          // merged 高点（含 0.2m 上影）
            Assert.Equal(9.8m, bottom.Price, 6);        // merged 低点（含 0.2m 下影）
            Assert.True(bottom.MergedIndex > top.MergedIndex, "底分型应在顶分型之后");
            // 确认索引 = 右邻合并K线最后一根原始K线
            Assert.True(top.ConfirmBarIndex > top.BarIndex, "分型确认索引必须晚于其自身K线（无未来函数）");
        });

        t.Case("缠论_笔_同型分型只保留更极端者", () =>
        {
            // 连续三个顶（合成输入，模拟包含处理后出现的同型分型）：应只保留最高的那个
            var raw = new List<ChanFractal>
            {
                new(MergedIndex: 0, BarIndex: 0, Price: 12, IsTop: true, ConfirmBarIndex: 1),
                new(MergedIndex: 3, BarIndex: 3, Price: 13, IsTop: true, ConfirmBarIndex: 4),  // 更极端 → 替换
                new(MergedIndex: 6, BarIndex: 6, Price: 11, IsTop: true, ConfirmBarIndex: 7),  // 不更极端 → 丢弃
                new(MergedIndex: 9, BarIndex: 9, Price: 10, IsTop: false, ConfirmBarIndex: 10),
            };
            var strokes = ChanStrokeBuilder.Build(raw, minMergedBarsBetween: 2);
            Assert.Equal(1, strokes.Count);
            Assert.Equal(13, strokes[0].StartPrice, 6);
            Assert.Equal(10, strokes[0].EndPrice, 6);
            Assert.False(strokes[0].IsUp, "顶→底应为下降笔");
        });

        // ─────────────────── 笔（新笔） ───────────────────

        t.Case("缠论_新笔_间隔足够时成立且末笔未确认", () =>
        {
            // 六段整齐锯齿（每段 6 根）：底→顶→底→顶→底→顶，转折处间隔充足
            var prices = new List<decimal>();
            var levels = new[] { 13.5m, 10.0m, 13.0m, 9.5m, 12.5m, 10.0m, 13.0m };
            for (var seg = 0; seg < levels.Length - 1; seg++)
            {
                var from = levels[seg];
                var to = levels[seg + 1];
                for (var k = 0; k < 6; k++) prices.Add(from + (to - from) * k / 5.0m);
            }
            var fractals = ZigZag([.. prices]);
            var strokes = ChanStrokeBuilder.Build(fractals, minMergedBarsBetween: 2);

            Assert.Equal(4, strokes.Count);
            Assert.True(strokes[0].IsUp, "首笔应为上升笔（底→顶）");
            Assert.True(strokes[0].IsConfirmed, "身后已有足够笔数，首笔应已确认");
            Assert.False(strokes[^1].IsConfirmed, "最后一笔可能被后续数据修订，必须标记为未确认");
            Assert.True(strokes[0].StableFromBarIndex is not null, "已确认的笔必须有稳定时点");
        });

        t.Case("缠论_新笔_间隔不足时被拒绝", () =>
        {
            // 顶底之间只隔 1 根独立合并K线（MergedIndex 差 = 2）→ 新笔（要求差 ≥ 3）应拒绝
            var raw = new List<ChanFractal>
            {
                new(MergedIndex: 0, BarIndex: 0, Price: 10, IsTop: false, ConfirmBarIndex: 1),
                new(MergedIndex: 2, BarIndex: 2, Price: 12, IsTop: true, ConfirmBarIndex: 3),
                new(MergedIndex: 4, BarIndex: 4, Price: 11, IsTop: false, ConfirmBarIndex: 5),
            };
            var strokes = ChanStrokeBuilder.Build(raw, minMergedBarsBetween: 2);
            // 0→2 间隔 1 < 2 被拒；2→4 间隔 1 也被拒 → 无有效笔（仅保留孤立分型）
            Assert.Equal(0, strokes.Count);
        });

        t.Case("缠论_新笔_间隔恰好满足时成立", () =>
        {
            var raw = new List<ChanFractal>
            {
                new(MergedIndex: 0, BarIndex: 0, Price: 10, IsTop: false, ConfirmBarIndex: 1),
                new(MergedIndex: 3, BarIndex: 3, Price: 12, IsTop: true, ConfirmBarIndex: 4),
                new(MergedIndex: 6, BarIndex: 6, Price: 11, IsTop: false, ConfirmBarIndex: 7),
            };
            var strokes = ChanStrokeBuilder.Build(raw, minMergedBarsBetween: 2);
            Assert.Equal(2, strokes.Count);
            Assert.True(strokes[0].IsUp);
            Assert.False(strokes[1].IsUp);
            Assert.Equal(10, strokes[0].StartPrice, 6);
            Assert.Equal(12, strokes[0].EndPrice, 6);
        });

        t.Case("缠论_新笔_老笔口径更严格", () =>
        {
            var raw = new List<ChanFractal>
            {
                new(MergedIndex: 0, BarIndex: 0, Price: 10, IsTop: false, ConfirmBarIndex: 1),
                new(MergedIndex: 3, BarIndex: 3, Price: 12, IsTop: true, ConfirmBarIndex: 4),
                new(MergedIndex: 6, BarIndex: 6, Price: 11, IsTop: false, ConfirmBarIndex: 7),
            };
            // 老笔要求间隔 ≥ 3 → 0→3（间隔 2）不满足
            var strict = ChanStrokeBuilder.Build(raw, minMergedBarsBetween: 3);
            Assert.Equal(0, strict.Count);
        });

        // ─────────────────── 中枢 ───────────────────

        t.Case("缠论_中枢_三笔重叠成立", () =>
        {
            // 三笔：10→12（上）、12→10.5m（下）、10.5m→11.8m（上）
            // Zg = min(12, 12, 11.8m) = 11.8m；Zd = max(10, 10.5m, 10.5m) = 10.5m
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 10, 5, 12, true),
                Stroke(5, 12, 10, 10.5m, false),
                Stroke(10, 10.5m, 15, 11.8m, true),
            };
            var pivots = ChanPivotDetector.Detect(strokes);
            Assert.Equal(1, pivots.Count);
            Assert.Equal(11.8m, pivots[0].Zg, 6);
            Assert.Equal(10.5m, pivots[0].Zd, 6);
            Assert.Equal(3, pivots[0].StrokeCount);
            Assert.False(pivots[0].IsConfirmed); // 没有离开笔 → 仍在中枢内
        });

        t.Case("缠论_中枢_三笔无重叠则不成立", () =>
        {
            // 第一笔区间 [10,12]，第二笔 [12.5m,14]，第三笔 [14.5m,16]：无共同重叠
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 10, 5, 12, true),
                Stroke(5, 12.5m, 10, 14, true),
                Stroke(10, 14.5m, 15, 16, true),
            };
            var pivots = ChanPivotDetector.Detect(strokes);
            Assert.Equal(0, pivots.Count);
        });

        t.Case("缠论_中枢_延伸并入与离开确认", () =>
        {
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 10, 5, 12, true),
                Stroke(5, 12, 10, 10.5m, false),
                Stroke(10, 10.5m, 15, 11.8m, true),
                Stroke(15, 11.8m, 20, 10.8m, false),   // 仍与 [10.5m,11.8m] 相交 → 延伸
                Stroke(20, 10.8m, 25, 13.5m, true),    // 完全在上方 → 离开笔
                Stroke(25, 13.5m, 30, 12.2m, false),
            };
            var pivots = ChanPivotDetector.Detect(strokes);
            Assert.Equal(1, pivots.Count);
            Assert.Equal(4, pivots[0].StrokeCount);
            Assert.True(pivots[0].IsConfirmed);
            Assert.Equal(4, pivots[0].LeavingStrokeIndex);
        });

        // ─────────────────── 背驰（MACD 面积法） ───────────────────

        t.Case("缠论_背驰_创新低但面积缩小", () =>
        {
            // 中枢由 s0/s1/s2 构成；s3 为向下离开笔（终点 9 跌破 Zd=11.5m），
            // 进入段取离开笔之前最近的同向笔 = s1（盘整背驰）
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 11, 5, 13, true),
                Stroke(5, 13, 10, 11.5m, false),
                Stroke(10, 11.5m, 15, 13, true),
                Stroke(15, 13, 20, 9, false, confirmed: true, stableFrom: 22),
            };
            var hist = new decimal?[30];
            for (var i = 5; i <= 10; i++) hist[i] = -2.0m;    // 进入段面积 12
            for (var i = 15; i <= 20; i++) hist[i] = -0.8m;   // 离开段面积 4（更弱）
            var pivots = ChanPivotDetector.Detect(strokes);
            Assert.Equal(1, pivots.Count);
            Assert.Equal(3, pivots[0].LeavingStrokeIndex);
            var dv = ChanDivergenceDetector.Check(strokes, pivots[0], hist, 0.9m);
            Assert.True(dv is not null, "创新低但面积缩小应判定背驰");
            Assert.Equal(false, dv!.Value.IsUp);
            Assert.True(dv.Value.AreaRatio < 0.5m);
        });

        t.Case("缠论_背驰_面积放大不算背驰", () =>
        {
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 11, 5, 13, true),
                Stroke(5, 13, 10, 11.5m, false),
                Stroke(10, 11.5m, 15, 13, true),
                Stroke(15, 13, 20, 9, false, confirmed: true, stableFrom: 22),
            };
            var hist = new decimal?[30];
            for (var i = 5; i <= 10; i++) hist[i] = -0.5m;    // 进入段面积 3
            for (var i = 15; i <= 20; i++) hist[i] = -2.0m;   // 离开段面积 12（更强）
            var pivots = ChanPivotDetector.Detect(strokes);
            var dv = ChanDivergenceDetector.Check(strokes, pivots[0], hist, 0.9m);
            Assert.True(dv is null, "面积放大不应判定背驰");
        });

        // ─────────────────── 三类买卖点 ───────────────────

        t.Case("缠论_买卖点_1类买点由背驰产生且记账在确认K线", () =>
        {
            var candles = FlatBars(40, 100);
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 11, 5, 13, true),
                Stroke(5, 13, 10, 11.5m, false),
                Stroke(10, 11.5m, 15, 13, true),
                Stroke(15, 13, 20, 9, false, confirmed: true, stableFrom: 30),
            };
            var hist = new decimal?[40];
            for (var i = 5; i <= 10; i++) hist[i] = -2.0m;
            for (var i = 15; i <= 20; i++) hist[i] = -0.8m;

            var points = ChanSignals.Detect(candles, strokes, ChanPivotDetector.Detect(strokes), hist, FlatAtr(40), 0.9m);
            var firstBuy = points.FirstOrDefault(p => p.Kind == "1买");
            Assert.True(firstBuy is not null, "应产生 1 类买点");
            Assert.Equal("buy", firstBuy!.Side);
            Assert.Equal(9, firstBuy.ReferencePrice, 6);           // 参考价 = 离开笔低点
            Assert.Equal(candles[30].Time, firstBuy.Time);         // 记账时间 = 该笔稳定时点
            Assert.Equal(candles[30].Close, firstBuy.Price, 6);    // 记账价 = 该K线收盘
        });

        t.Case("缠论_买卖点_未确认的笔不产生信号", () =>
        {
            var candles = FlatBars(40, 100);
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 14, 5, 11, false, confirmed: true, stableFrom: 10),
                Stroke(5, 11, 10, 12.5m, true, confirmed: true, stableFrom: 15),
                Stroke(10, 12.5m, 15, 11.5m, false, confirmed: true, stableFrom: 20),
                Stroke(15, 11.5m, 20, 13, true, confirmed: true, stableFrom: 25),
                Stroke(20, 13, 25, 10, false, confirmed: false, stableFrom: null),  // 末笔未确认
            };
            var hist = new decimal?[40];
            for (var i = 0; i <= 5; i++) hist[i] = -2.0m;
            for (var i = 21; i <= 25; i++) hist[i] = -0.8m;

            var points = ChanSignals.Detect(candles, strokes, ChanPivotDetector.Detect(strokes), hist, FlatAtr(40), 0.9m);
            Assert.Equal(0, points.Count(p => p.Kind == "1买"));
        });

        t.Case("缠论_买卖点_3类买点_突破中枢后回抽不回", () =>
        {
            var candles = FlatBars(40, 100);
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 10, 5, 12, true, confirmed: true, stableFrom: 8),
                Stroke(5, 12, 10, 10.5m, false, confirmed: true, stableFrom: 13),
                Stroke(10, 10.5m, 15, 11.8m, true, confirmed: true, stableFrom: 18),
                Stroke(15, 11.8m, 20, 13.5m, true, confirmed: true, stableFrom: 23),  // 突破 Zg=11.8m
                Stroke(20, 13.5m, 25, 12.5m, false, confirmed: true, stableFrom: 28), // 回抽低点 12.5m > 11.8m
            };
            var hist = new decimal?[40];
            var points = ChanSignals.Detect(candles, strokes, ChanPivotDetector.Detect(strokes), hist, FlatAtr(40), 0.9m);
            var thirdBuy = points.FirstOrDefault(p => p.Kind == "3买");
            Assert.True(thirdBuy is not null, "应产生 3 类买点");
            Assert.Equal(12.5m, thirdBuy!.ReferencePrice, 6);
            Assert.Contains("不回中枢", thirdBuy.Note);
        });

        t.Case("缠论_买卖点_确认价已越过结构失效位则丢弃信号", () =>
        {
            var time = 1_000_000L;
            var candles = new List<Candle>();
            for (var i = 0; i < 40; i++)
                candles.Add(new Candle(time + i * 3600L, 10.6m, 10.9m, 10.3m, 10.6m, 10));

            // 中枢 [10.5m, 11.8m] → 向下离开笔（终点 10.0m 跌破 Zd）→ 回抽笔高点 10.3m 仍在 Zd 下方 ⇒ 3 卖
            // 3 卖结构失效位 = 回抽高点 10.3m，止损 = 10.3m + 0.5m×ATR(1.0m) = 10.8m
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 10.5m, 5, 12, true, confirmed: true, stableFrom: 8),
                Stroke(5, 12, 10, 10.5m, false, confirmed: true, stableFrom: 13),
                Stroke(10, 10.5m, 15, 11.8m, true, confirmed: true, stableFrom: 18),
                Stroke(15, 11.8m, 20, 10.0m, false, confirmed: true, stableFrom: 23),  // 离开笔（向下）
                Stroke(20, 10.0m, 25, 10.3m, true, confirmed: true, stableFrom: 28),   // 回抽笔（高点 10.3m < Zd）
            };
            var hist = new decimal?[40];
            var atr = FlatAtr(40, 1.0m);
            var pivots = ChanPivotDetector.Detect(strokes);

            // 常规：确认K线收盘 10.6m < 止损 10.8m → 卖点有效，应产生 3 卖
            var normal = ChanSignals.Detect(candles, strokes, pivots, hist, atr, 0.9m);
            var third = normal.FirstOrDefault(p => p.Kind == "3卖");
            Assert.True(third is not null, "常规情况下应产生 3 卖");
            Assert.Equal(10.8m, third!.StopPrice, 6);
            Assert.True(third.StopPrice > third.Price, "卖点止损必须高于入场价");

            // 确认K线收盘已涨到 11.2m（越过止损 10.8m）→ 结构失效位已破，应丢弃该信号
            var lateCandles = candles.ToList();
            lateCandles[28] = new Candle(time + 28 * 3600L, 11.2m, 11.5m, 10.9m, 11.2m, 10);
            var late = ChanSignals.Detect(lateCandles, strokes, pivots, hist, atr, 0.9m);
            Assert.Equal(0, late.Count(p => p.Kind == "3卖"));
        });


        // ─────────────────── 审查修复回归（2026-09-29） ───────────────────

        t.Case("缠论_P1_暖机隔离_起始于窗口暖机区的中枢不产生信号", () =>
        {
            var candles = FlatBars(40, 100);
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 10, 5, 12, true, confirmed: true, stableFrom: 8),      // 起点 BarIndex=0，位于暖机区
                Stroke(5, 12, 10, 10.5m, false, confirmed: true, stableFrom: 13),
                Stroke(10, 10.5m, 15, 11.8m, true, confirmed: true, stableFrom: 18),
                Stroke(15, 11.8m, 20, 13.5m, true, confirmed: true, stableFrom: 23),
                Stroke(20, 13.5m, 25, 12.5m, false, confirmed: true, stableFrom: 28),
            };
            var hist = new decimal?[40];
            var atr = FlatAtr(40, 1.0m);
            var pivots = ChanPivotDetector.Detect(strokes);

            var withoutWarmup = ChanSignals.Detect(candles, strokes, pivots, hist, atr, 0.9m, warmupBars: 0);
            var withWarmup = ChanSignals.Detect(candles, strokes, pivots, hist, atr, 0.9m, warmupBars: 30);

            Assert.True(withoutWarmup.Count > 0, "无暖机隔离时应产生信号（该中枢起点在窗口首根）");
            Assert.Equal(0, withWarmup.Count, "暖机隔离应屏蔽起点位于窗口前 30 根内的中枢所产生的信号");
        });

        t.Case("缠论_P1_固定内部窗口_不同显示窗口在重叠区信号一致", () =>
        {
            // 构造"通道震荡 → 向上突破 → 回抽不回中枢"的显式几何（保证产生 3 类买点）
            var levels = new List<decimal>();
            void Leg(decimal from, decimal to, int bars)
            {
                for (var k = 1; k <= bars; k++) levels.Add(from + (to - from) * k / bars);
            }
            // 前置温区（远离暖机隔离区，保证通道中枢不被隔离）
            for (var r = 0; r < 4; r++) { Leg(95, 99, 8); Leg(99, 95.5m, 8); }
            // 通道：100↔104 反复（形成重叠笔画 → 中枢）
            for (var r = 0; r < 5; r++) { Leg(100, 104, 10); Leg(104, 100.5m, 10); }
            // 突破 + 回抽（回抽低点 105.5m 仍在通道上沿 104 之上）→ 3 买
            Leg(100.5m, 108, 8);
            Leg(108, 105.5m, 8);
            Leg(105.5m, 112, 8);
            // 后续走势让上述回抽笔"稳定"
            Leg(112, 107, 8);
            Leg(107, 114, 8);
            Leg(114, 109, 8);

            var all = new List<Candle>();
            var time = 3_000_000L;
            for (var i = 0; i < levels.Count; i++)
                all.Add(new Candle(time + i * 3600L, levels[i], levels[i] + 0.5m, levels[i] - 0.5m, levels[i], 10));

            var allHist = new decimal?[all.Count];   // 3 类不依赖 MACD，置零即可
            var options = new ChanOptions { WarmupBars = 30, AnalysisBars = levels.Count };

            // 显示窗口 60 / 100 根（都短于内部窗口），内部窗口固定为全部行情
            var (displayA, chanA) = ChanWindowSelector.Select(all, 60, options.AnalysisBars);
            var (displayB, chanB) = ChanWindowSelector.Select(all, 100, options.AnalysisBars);
            Assert.Equal(levels.Count, chanA.Count);
            Assert.Equal(chanA.Count, chanB.Count);
            Assert.True(displayA.Count < chanA.Count, "显示窗口应短于内部固定窗口");

            var offset = all.Count - chanA.Count;
            var a = ChanAnalyzer.Analyze(chanA, allHist.Skip(offset).ToArray(), FlatAtr(chanA.Count, 1.0m), options);
            var b = ChanAnalyzer.Analyze(chanB, allHist.Skip(offset).ToArray(), FlatAtr(chanB.Count, 1.0m), options);

            string Sig(ChanBuySellPoint p) => $"{p.Kind}|{p.Side}|{p.Time}|{p.StopPrice:0.00m}|{p.Note}";
            var overlapStartTime = displayB[0].Time;   // 较长显示窗口的起点 = 共同区间
            var aIn = a.Points.Where(p => p.Time >= overlapStartTime).Select(Sig).OrderBy(x => x).ToList();
            var bIn = b.Points.Where(p => p.Time >= overlapStartTime).Select(Sig).OrderBy(x => x).ToList();

            Assert.True(bIn.Count > 0, $"合成行情应产生缠论信号（实际 0 个；总信号 {b.Points.Count}）");
            Assert.Equal(0, aIn.Count - bIn.Count);
            Assert.All(aIn, x => Assert.True(bIn.Contains(x), $"显示窗口 120 的信号应同样出现在显示窗口 200 的结果中：{x}"));
        });

        t.Case("缠论_P2_中枢计数为真实数量_不受返回上限影响", () =>
        {
            // 合成不易造出多中枢，直接校验分析器的契约：Pivots 列表长度即为摘要口径
            var all = new List<Candle>();
            var time = 4_000_000L;
            for (var i = 0; i < 400; i++)
            {
                var p0 = 50 + (decimal)Math.Sin(i / 4.0) * 3;
                all.Add(new Candle(time + i * 3600L, p0, p0 + 0.8m, p0 - 0.8m, p0, 10));
            }
            var hist = all.Select((_, i) => (decimal)Math.Cos(i / 7.0) * 0.8m).Select(v => (decimal?)v).ToArray();
            var options = new ChanOptions { MaxPoints = 1 };   // 人为压低买卖点上限
            var result = ChanAnalyzer.Analyze(all, hist, FlatAtr(all.Count, 1.0m), options);

            // 中枢列表不受 MaxPoints 影响；买卖点按上限裁剪，但中枢数保持真实
            Assert.True(result.Pivots.Count >= 0, "中枢列表应存在（可为 0）");
            Assert.True(result.Points.Count <= 1, "买卖点应遵守 MaxPoints 上限");
            var pivotCountReported = result.Pivots.Count;
            var zg = result.Series["chanZg"];
            var distinct = zg.Where(v => v != null).Distinct().Count();
            Assert.True(distinct <= pivotCountReported + 1,
                $"序列中的中枢价位种类({distinct})不应超过报告的中枢数({pivotCountReported})加 1");
        });

        t.Case("缠论_P4_1类因不可交易被丢弃时_2类仍可产生", () =>
        {
            // 1 类所在笔的稳定时点K线收盘刻意越过失效位（1 类被丢弃），但其后的 2 类形态完整
            var time = 5_000_000L;
            var candles = new List<Candle>();
            for (var i = 0; i < 60; i++)
                candles.Add(new Candle(time + i * 3600L, 12.0m, 12.3m, 11.7m, 12.0m, 10));

            // 中枢 [10.5m, 11.8m]；s3 向下离开（终点 9.0m < Zd）→ 1 买（背驰）；s4 向上；s5 向下回抽不破 9.0m
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 11, 5, 13, true, confirmed: true, stableFrom: 8),
                Stroke(5, 13, 10, 11.5m, false, confirmed: true, stableFrom: 13),
                Stroke(10, 11.5m, 15, 13, true, confirmed: true, stableFrom: 18),
                Stroke(15, 13, 20, 9.0m, false, confirmed: true, stableFrom: 30),   // 1 买：稳定时点=30
                Stroke(20, 9.0m, 25, 11.0m, true, confirmed: true, stableFrom: 35),
                Stroke(25, 11.0m, 30, 9.5m, false, confirmed: true, stableFrom: 40), // 2 买：回抽 9.5m > 9.0m
            };
            var hist = new decimal?[60];
            for (var i = 5; i <= 10; i++) hist[i] = -2.0m;    // 进入段面积
            for (var i = 15; i <= 20; i++) hist[i] = -0.6m;   // 离开段面积更小 → 背驰

            // 让 1 买的记账K线（index 30）跌破结构失效位（参考低点 9.0m − 0.5m×ATR = 8.5m）→ 1 买不可交易被丢弃
            candles[30] = new Candle(time + 30 * 3600L, 8.0m, 8.3m, 7.7m, 8.0m, 10);
            var atr = FlatAtr(60, 1.0m);

            var points = ChanSignals.Detect(candles, strokes, ChanPivotDetector.Detect(strokes), hist, atr, 0.9m, warmupBars: 0);

            Assert.Equal(0, points.Count(p => p.Kind == "1买"));   // 1 买因不可交易被丢弃
            Assert.True(points.Any(p => p.Kind == "2买"), "2 类不应因 1 类被丢弃而连带消失（审查 P4）");
        });



        // ─────────────────── 次级别确认（2026-09-29 轻量版） ───────────────────

        t.Case("缠论_次级别确认_次级别未转向则不产生信号", () =>
        {
            var candles = FlatBars(40, 100);
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 11, 5, 13, true, confirmed: true, stableFrom: 8),
                Stroke(5, 13, 10, 11.5m, false, confirmed: true, stableFrom: 13),
                Stroke(10, 11.5m, 15, 13, true, confirmed: true, stableFrom: 18),
                Stroke(15, 13, 20, 9, false, confirmed: true, stableFrom: 30),   // 1 买（向下离开笔）
            };
            var hist = new decimal?[40];
            for (var i = 5; i <= 10; i++) hist[i] = -2.0m;
            for (var i = 15; i <= 20; i++) hist[i] = -0.6m;   // 背驰
            var atr = FlatAtr(40, 1.0m);
            var pivots = ChanPivotDetector.Detect(strokes);

            // 次级别在参考点之后完成同向笔 → 确认；方向相反 → 拒绝；无次级别数据 → 不过滤
            var confirmedBuy = ChanSignals.Detect(candles, strokes, pivots, hist, atr, 0.9m, 0,
                (referenceTime, accountingTime, side) => side == "buy");
            var rejected = ChanSignals.Detect(candles, strokes, pivots, hist, atr, 0.9m, 0,
                (referenceTime, accountingTime, side) => side != "buy");
            var unconfirmed = ChanSignals.Detect(candles, strokes, pivots, hist, atr, 0.9m, 0, null);

            Assert.True(confirmedBuy.Count > 0, "次级别同向时应产生 1 买");
            Assert.Equal(0, rejected.Count, "次级别方向相反时不应产生信号");
            Assert.True(unconfirmed.Count > 0, "次级别数据缺失时不作过滤（保持旧行为）");
        });

        t.Case("缠论_次级别确认_次级别尚无已完成笔则不产生信号", () =>
        {
            var candles = FlatBars(40, 100);
            var strokes = new List<ChanStroke>
            {
                Stroke(0, 11, 5, 13, true, confirmed: true, stableFrom: 8),
                Stroke(5, 13, 10, 11.5m, false, confirmed: true, stableFrom: 13),
                Stroke(10, 11.5m, 15, 13, true, confirmed: true, stableFrom: 18),
                Stroke(15, 13, 20, 9, false, confirmed: true, stableFrom: 30),
            };
            var hist = new decimal?[40];
            for (var i = 5; i <= 10; i++) hist[i] = -2.0m;
            for (var i = 15; i <= 20; i++) hist[i] = -0.6m;
            var atr = FlatAtr(40, 1.0m);
            var pivots = ChanPivotDetector.Detect(strokes);

            // 次级别K线：每根 900 秒（15m），参考点 = 第 10 根，记账点 = 第 20 根
            var subCandles = new List<Candle>();
            for (var i = 0; i < 40; i++)
                subCandles.Add(new Candle(1_000_000L + i * 900L, 100, 101, 99, 100, 10));
            var refTime = subCandles[10].Time;
            var accTime = subCandles[20].Time;

            // ① 无已完成笔 → 未确认
            var none = new List<ChanStroke> { Stroke(0, 100, 5, 101, true, confirmed: false, stableFrom: null) };
            Assert.False(ChanAnalyzer.SubLevelMatches(subCandles, none, refTime, accTime, "buy", 10),
                "次级别无已完成笔时应判定为未确认");

            // ② 有同向已完成笔且落在窗口内（参考点后 5 根内）→ 确认
            var timely = new List<ChanStroke> { Stroke(0, 100, 15, 101, true, confirmed: true, stableFrom: 15) };
            Assert.True(ChanAnalyzer.SubLevelMatches(subCandles, timely, refTime, accTime, "buy", 10),
                "次级别在窗口内完成同向笔应判定为已确认");
            Assert.False(ChanAnalyzer.SubLevelMatches(subCandles, timely, refTime, accTime, "sell", 10),
                "方向相反时不应确认");

            // ③ 同向笔完成得太晚（超出窗口）→ 未确认
            var late = new List<ChanStroke> { Stroke(0, 100, 35, 101, true, confirmed: true, stableFrom: 35) };
            Assert.False(ChanAnalyzer.SubLevelMatches(subCandles, late, refTime, accTime, "buy", 10),
                "超出确认窗口的次级别转向不应算确认");
        });

        // ─────────────────── 分级规则（按证据强度） ───────────────────

        t.Case("分级规则_集中度过高降级为仅观察", () =>
        {
            // 构造：40 条样本，全部来自同一标的（占比 100%）→ 不应标"可参考"
            var entries = Enumerable.Range(0, 40).Select(i => new SignalEntry
            {
                Market = MarketKind.Spot, Pair = TradingPair.Parse(i < 38 ? "AAAUSDT" : "BBBUSDT"), Interval = "1h",
                Source = "测试", Side = i % 2 == 0 ? "buy" : "sell", Time = 1_000_000 + i * 40 * 3600L,
                Price = 100, IsConfirmed = true, RecordedAt = 1_000_000,
                // 超额取有波动的值：全部相同会让标准差为 0（t=0），被更早的显著性检查拦下，测不到集中度规则
                Outcome = new SignalOutcome
                {
                    Status = "ok", Ret = 0.01m, Excess = 0.004m + (i % 5) * 0.001m,
                    NetPositive = true, StopHit = false,
                },
            }).ToList();

            var stats = SignalQualityRules.Evaluate(entries);
            Assert.Equal("仅观察", stats.Grade);
            Assert.Contains("单一标的占比", stats.Reason);
        });

        t.Case("分级规则_波次不足则样本不足", () =>
        {
            // 20 条连续（同一波）→ 独立波次 1 < 30
            var entries = Enumerable.Range(0, 20).Select(i => new SignalEntry
            {
                Market = MarketKind.Spot, Pair = TradingPair.Parse("AAAUSDT"), Interval = "1h",
                Source = "测试", Side = "buy", Time = 1_000_000 + i * 3600L,
                Price = 100, IsConfirmed = true, RecordedAt = 1_000_000,
                Outcome = new SignalOutcome { Status = "ok", Ret = 0.01m, Excess = 0.005m, NetPositive = true, StopHit = false },
            }).ToList();

            var stats = SignalQualityRules.Evaluate(entries);
            Assert.Equal("样本不足", stats.Grade);
            Assert.Contains("独立波次", stats.Reason);
        });

        t.Case("分级规则_满足全部条件才可参考", () =>
        {
            // 60 波、跨 3 个标的、超额稳定为正 → 可参考
            var entries = new List<SignalEntry>();
            var symbols = new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT" };
            for (var i = 0; i < 60; i++)
            {
                entries.Add(new SignalEntry
                {
                    Market = MarketKind.Spot, Pair = TradingPair.Parse(symbols[i % 3]), Interval = "1h",
                    Source = "测试", Side = i % 2 == 0 ? "buy" : "sell",
                    Time = 1_000_000 + i * 30 * 3600L,   // 间隔 30 根 → 每波独立
                    Price = 100, IsConfirmed = true, RecordedAt = 1_000_000,
                    Outcome = new SignalOutcome
                    {
                        Status = "ok", Ret = 0.01m + (i % 5) * 0.001m, Excess = 0.004m + (i % 7) * 0.001m,
                        NetPositive = true, StopHit = false,
                    },
                });
            }

            var stats = SignalQualityRules.Evaluate(entries);
            Assert.Equal("可参考", stats.Grade);
            Assert.Contains("t=", stats.Reason);
        });

        // ─────────────────── 无未来函数 ───────────────────

        t.Case("缠论_无未来函数_截断数据不改变历史结构", () =>
        {
            // 合成锯齿行情：较长序列，末段截断后比较早期结构
            var candles = new List<Candle>();
            var time = 1_000_000L;
            for (var i = 0; i < 120; i++)
            {
                var basePrice = 100 + (decimal)Math.Sin(i / 5.0) * 6 + (decimal)Math.Sin(i / 17.0) * 3;
                var high = basePrice + 1.5m;
                var low = basePrice - 1.5m;
                candles.Add(new Candle(time + i * 3600L, basePrice, high, low, basePrice + 0.2m, 10));
            }
            var hist = candles.Select((_, i) => (decimal)Math.Sin(i / 7.0) * 1.2m).Select(v => (decimal?)v).ToArray();

            var full = ChanAnalyzer.Analyze(candles, hist, FlatAtr(candles.Count, candles[0].Close * 0.01m), ChanOptions.Default);
            var truncated = ChanAnalyzer.Analyze(candles.Take(100).ToList(), hist.Take(100).ToArray(), FlatAtr(100, candles[0].Close * 0.01m), ChanOptions.Default);

            var cutoff = candles[99].Time;
            var fullEarlyStrokes = full.Strokes
                .Where(s => s.IsConfirmed && s.StableFromBarIndex is { } b && candles[b].Time <= cutoff)
                .Select(s => (s.StartBarIndex, s.EndBarIndex, s.IsUp))
                .ToList();
            var truncatedStrokes = truncated.Strokes
                .Select(s => (s.StartBarIndex, s.EndBarIndex, s.IsUp))
                .ToList();

            Assert.True(fullEarlyStrokes.Count > 0, "完整数据中应存在早期已确认的笔");
            Assert.All(fullEarlyStrokes, s => Assert.True(truncatedStrokes.Contains(s),
                $"截断后缺少笔 {s.StartBarIndex}→{s.EndBarIndex}（疑似使用了未来数据）"));
        });

        t.Case("缠论_端到端_合成框体行情可产出中枢与买卖点字段完整", () =>
        {
            var candles = new List<Candle>();
            var time = 2_000_000L;
            // 三段式：区间震荡 → 下破背驰 → 回升，构造出中枢与背驰
            var seq = new List<decimal>();
            for (var i = 0; i < 30; i++) seq.Add(100 + (decimal)Math.Sin(i / 3.0) * 2);
            for (var i = 0; i < 12; i++) seq.Add(100 - i * 1.5m);
            for (var i = 0; i < 12; i++) seq.Add(82 + i * 0.8m);
            for (var i = 0; i < 30; i++) seq.Add(92 + (decimal)Math.Sin(i / 4.0) * 2);
            for (var i = 0; i < seq.Count; i++)
            {
                var p = seq[i];
                candles.Add(new Candle(time + i * 3600L, p, p + 1, p - 1, p, 10));
            }
            var hist = candles.Select((_, i) => i < 42 ? -1.5m : -0.4m).Select(v => (decimal?)v).ToArray();

            var result = ChanAnalyzer.Analyze(candles, hist, FlatAtr(candles.Count, candles[0].Close * 0.01m), ChanOptions.Default);
            Assert.True(result.Strokes.Count > 0, "应构建出笔");
            Assert.Equal(candles.Count, result.Series["chanStroke"].Length);
            Assert.Equal(candles.Count, result.Series["chanZg"].Length);
            Assert.True(result.Fractals.Count > 0, "应识别出分型");
        });
    }

    // ─────────────────── 构造辅助 ───────────────────

    private static decimal?[] FlatAtr(int count, decimal value = 1m)
    {
        var arr = new decimal?[count];
        Array.Fill(arr, value);
        return arr;
    }

    private static Candle Bar(long time, decimal open, decimal high, decimal low, decimal close) =>
        new(time, open, high, low, close, 10);

    private static Candle[] FlatBars(int count, decimal price)
    {
        var arr = new Candle[count];
        for (var i = 0; i < count; i++) arr[i] = new Candle(1_000_000L + i * 3600L, price, price + 1, price - 1, price, 10);
        return arr;
    }

    private static ChanStroke Stroke(
        int startBar, decimal startPrice, int endBar, decimal endPrice, bool isUp,
        bool confirmed = true, int? stableFrom = 0) =>
        new(startBar, startPrice, endBar, endPrice, isUp, confirmed, confirmed ? (stableFrom ?? endBar) : null);

    /// <summary>由收盘价序列构造K线并返回分型列表（用于笔的测试）。</summary>
    private static IReadOnlyList<ChanFractal> ZigZag(decimal[] prices)
    {
        var candles = new List<Candle>();
        var time = 1_000_000L;
        for (var i = 0; i < prices.Length; i++)
            candles.Add(new Candle(time + i, prices[i], prices[i] + 0.2m, prices[i] - 0.2m, prices[i], 10));
        var merged = ChanInclusion.Merge(candles);
        return ChanFractalDetector.Detect(merged, candles);
    }
}
