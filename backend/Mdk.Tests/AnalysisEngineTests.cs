using Mdk.Api.Analysis;
using Mdk.Api.Domain;
using Mdk.Api.Indicators;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>分析引擎：趋势打分、点位聚类、信号规则（全部注入合成数据，不依赖网络）。</summary>
public static class AnalysisEngineTests
{
    private static Candle[] FlatCandles(int count, decimal price = 100) =>
        Enumerable.Range(0, count)
            .Select(i => new Candle(1000 + i * 60L, price, price + 1, price - 1, price, 10))
            .ToArray();

    public static void Register(TestKit t)
    {
        // ---------- 趋势方向 ----------

        t.Case("趋势_单边多头_判LONG满分", () =>
        {
            var candles = FlatCandles(1, 110);
            decimal?[] ema50 = [105];
            decimal?[] ema200 = [100];
            var dmi = new AdxDmi.AdxDmiResult([25], [10], [30]);

            var trend = TrendAnalyzer.Analyze(candles, ema50, ema200, dmi);

            Assert.Equal("LONG", trend.Direction);
            Assert.Equal(100, trend.Score);
            Assert.Contains(trend.Reasons, r => r.Contains("EMA200 上方"));
        });

        t.Case("趋势_单边空头_判SHORT", () =>
        {
            var candles = FlatCandles(1, 90);
            decimal?[] ema50 = [95];
            decimal?[] ema200 = [110];
            var dmi = new AdxDmi.AdxDmiResult([10], [25], [30]);

            var trend = TrendAnalyzer.Analyze(candles, ema50, ema200, dmi);

            Assert.Equal("SHORT", trend.Direction);
            Assert.Equal(0, trend.Score);
        });

        t.Case("趋势_ADX弱_给出观望提示", () =>
        {
            var candles = FlatCandles(1, 101);
            decimal?[] ema50 = [100.5m];
            decimal?[] ema200 = [100];
            var dmi = new AdxDmi.AdxDmiResult([12], [10], [15]);

            var trend = TrendAnalyzer.Analyze(candles, ema50, ema200, dmi);

            Assert.Contains(trend.Reasons, r => r.Contains("趋势强度弱"));
        });

        // ---------- 关键点位 ----------

        t.Case("点位_三角波_摆动高低点聚合并分支撑阻力", () =>
        {
            // 三角波：每 12 根一个周期，峰 110（i%12==3），谷 90（i%12==9）
            const int count = 48;
            var candles = new Candle[count];
            for (var i = 0; i < count; i++)
            {
                var phase = i % 12;
                var mid = phase switch
                {
                    0 => 100, 1 => 104, 2 => 108, 3 => 110, 4 => 108, 5 => 104,
                    6 => 100, 7 => 96, 8 => 92, 9 => 90, 10 => 92, _ => 96,
                };
                candles[i] = new Candle(1000 + i * 60L, mid, mid + 0.5m, mid - 0.5m, mid, 10);
            }
            decimal?[] atr = Enumerable.Repeat((decimal?)5.0m, count).ToArray();

            var levels = SupportResistance.FindLevels(candles, atr);
            var lastClose = candles[^1].Close; // 96

            var resistance = levels.Where(l => l.Kind == "resistance").ToList();
            var support = levels.Where(l => l.Kind == "support").ToList();

            var top = Assert.Single(resistance);
            Assert.Equal(110.5m, top.Price, 1);
            Assert.Equal(4, top.Strength); // 峰出现 4 次（i=3,15,27,39）

            Assert.Contains(support, s => Math.Abs(s.Price - 89.5m) < 1);
            Assert.All(support, s => s.Price < lastClose);
        });

        // ---------- 买卖点信号 ----------

        t.Case("信号_三类金叉_生成买点并附ATR止损", () =>
        {
            var candles = FlatCandles(30);
            decimal?[] ema20 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] ema50 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] rsi = Enumerable.Repeat((decimal?)50.0m, 30).ToArray();
            decimal?[] dif = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] dea = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] atr = Enumerable.Repeat((decimal?)1.0m, 30).ToArray();

            for (var i = 0; i <= 10; i++) ema20[i] = 99;  // 前段已在下方，避免反向穿越
            ema20[11] = 101;                              // EMA20 上穿 EMA50 @11
            rsi[19] = 28; rsi[20] = 28; rsi[21] = 32;     // RSI 超卖回升 @21
            for (var i = 0; i <= 5; i++) dif[i] = -1;     // 前段已在下方
            dif[6] = 0.5m;                                 // MACD 金叉 @6
            atr[11] = 2; atr[21] = 1; atr[6] = 0.5m;

            var signals = SignalEngine.Generate(candles, ema20, ema50, rsi, dif, dea, atr);

            Assert.Equal(3, signals.Count);
            Assert.All(signals, s => Assert.Equal("buy", s.Side));

            var macdSignal = signals.Single(s => s.Source == "MACD");
            Assert.Equal(candles[6].Time, macdSignal.Time);
            Assert.Equal(99.0m, macdSignal.StopPrice!.Value, 10); // 100 − 2×0.5m

            var emaSignal = signals.Single(s => s.Source == "EMA");
            Assert.Equal(96.0m, emaSignal.StopPrice!.Value, 10);  // 100 − 2×2

            var rsiSignal = signals.Single(s => s.Source == "RSI");
            Assert.Equal(98.0m, rsiSignal.StopPrice!.Value, 10);
            Assert.Contains("超卖", rsiSignal.Note);
        });

        t.Case("信号_三类死叉_生成卖点_止损在上方", () =>
        {
            var candles = FlatCandles(30);
            decimal?[] ema20 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] ema50 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] rsi = Enumerable.Repeat((decimal?)50.0m, 30).ToArray();
            decimal?[] dif = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] dea = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] atr = Enumerable.Repeat((decimal?)1.0m, 30).ToArray();

            for (var i = 0; i <= 10; i++) ema20[i] = 101; // 前段已在上方
            ema20[11] = 99;                               // EMA20 下穿 EMA50 @11
            rsi[19] = 72; rsi[20] = 72; rsi[21] = 68;     // RSI 超买回落 @21
            for (var i = 0; i <= 5; i++) dif[i] = 1;      // 前段已在上方
            dif[6] = -0.5m;                                // MACD 死叉 @6

            var signals = SignalEngine.Generate(candles, ema20, ema50, rsi, dif, dea, atr);

            Assert.Equal(3, signals.Count);
            Assert.All(signals, s => Assert.Equal("sell", s.Side));
            Assert.All(signals, s => s.StopPrice!.Value > s.Price); // 卖点止损在上方
        });

            t.Case("信号_v2_冷却去重_同源同向6根内不重复", () =>
        {
            var candles = FlatCandles(30);
            decimal?[] ema20 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] ema50 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] rsi = Enumerable.Repeat((decimal?)50.0m, 30).ToArray();
            decimal?[] dif = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] dea = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] atr = Enumerable.Repeat((decimal?)1.0m, 30).ToArray();

            // 快线：0-4=99（下方），5=101 金叉，6=99 死叉，7-8=99，9=101 再次金叉（距上次买点 4 根 < 6 → 应被抑制）
            for (var i = 0; i <= 4; i++) ema20[i] = 99;
            ema20[5] = 101;
            ema20[6] = 99;
            ema20[7] = 99;
            ema20[8] = 99;
            ema20[9] = 101;

            var signals = SignalEngine.Generate(candles, ema20, ema50, rsi, dif, dea, atr);

            var emaSignals = signals.Where(s => s.Source == "EMA").ToList();
            Assert.Equal(2, emaSignals.Count); // 买@5 + 卖@6；9 处的买点被冷却抑制
            Assert.Equal("buy", emaSignals[0].Side);
            Assert.Equal("sell", emaSignals[1].Side);
            Assert.All(emaSignals, s => Assert.True(s.IsConfirmed));
        });

        t.Case("信号_v2_最后一根未收盘_只产生盘中预警", () =>
        {
            const int n = 30;
            var candles = new Candle[n];
            for (var i = 0; i < n; i++)
                candles[i] = new Candle(1_000_000L + i * 60L, 100, 101, 99, 100, 10);
            decimal?[] ema20 = Enumerable.Repeat((decimal?)100.0m, n).ToArray();
            decimal?[] ema50 = Enumerable.Repeat((decimal?)100.0m, n).ToArray();
            decimal?[] rsi = Enumerable.Repeat((decimal?)50.0m, n).ToArray();
            decimal?[] dif = Enumerable.Repeat((decimal?)0.0m, n).ToArray();
            decimal?[] dea = Enumerable.Repeat((decimal?)0.0m, n).ToArray();
            decimal?[] atr = Enumerable.Repeat((decimal?)1.0m, n).ToArray();

            // 前段都在慢线下方，交叉恰好发生在最后一根（未收盘）K线 → 只能有盘中预警
            for (var i = 0; i <= 28; i++) ema20[i] = 99;
            ema20[29] = 101;

            var signals = SignalEngine.Generate(candles, ema20, ema50, rsi, dif, dea, atr);

            var preview = signals.Where(s => !s.IsConfirmed).ToList();
            var confirmed = signals.Where(s => s.IsConfirmed).ToList();
            Assert.Equal(1, preview.Count);
            Assert.Equal("buy", preview[0].Side);
            Assert.Equal("EMA", preview[0].Source);
            Assert.Equal(0, confirmed.Count); // 同样的交叉不允许绕过收盘确认
        });

        t.Case("信号_v2_多周期共振标记_顺大势为true", () =>
        {
            // K线时间从 1,000,000 起，保证高周期K线在信号时刻已收盘
            const int n = 30;
            var candles = new Candle[n];
            for (var i = 0; i < n; i++)
                candles[i] = new Candle(1_000_000L + i * 60L, 100, 101, 99, 100, 10);
            decimal?[] ema20 = Enumerable.Repeat((decimal?)100.0m, n).ToArray();
            decimal?[] ema50 = Enumerable.Repeat((decimal?)100.0m, n).ToArray();
            decimal?[] rsi = Enumerable.Repeat((decimal?)50.0m, n).ToArray();
            decimal?[] dif = Enumerable.Repeat((decimal?)0.0m, n).ToArray();
            decimal?[] dea = Enumerable.Repeat((decimal?)0.0m, n).ToArray();
            decimal?[] atr = Enumerable.Repeat((decimal?)1.0m, n).ToArray();

            rsi[19] = 28; rsi[20] = 28; rsi[21] = 32;        // RSI 买点 @21
            for (var i = 0; i <= 10; i++) ema20[i] = 101;    // 前段在上方
            ema20[11] = 99;                                   // EMA 死叉卖点 @11

            // 高周期（4h）单边上涨：买点顺大势 true，卖点逆大势 false
            var htf = Enumerable.Range(0, 60)
                .Select(j => new Candle(800_000L + j * 600L, 100 + j, 101 + j, 99 + j, 100.5m + j, 10))
                .ToArray();

            var signals = SignalEngine.Generate(candles, ema20, ema50, rsi, dif, dea, atr,
                cooldownBars: 6, htfCandles: htf, htfInterval: "4h");

            Assert.Equal(2, signals.Count);
            var buy = signals.Single(s => s.Side == "buy");
            var sell = signals.Single(s => s.Side == "sell");
            Assert.Equal(true, buy.TrendAligned);
            Assert.Equal(false, sell.TrendAligned);
        });

        // ---------- v3：市场状态注入与结构确认信号 ----------

        t.Case("信号_v3_市场状态随信号记录", () =>
        {
            var candles = FlatCandles(30);
            decimal?[] ema20 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] ema50 = Enumerable.Repeat((decimal?)100.0m, 30).ToArray();
            decimal?[] rsi = Enumerable.Repeat((decimal?)50.0m, 30).ToArray();
            decimal?[] dif = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] dea = Enumerable.Repeat((decimal?)0.0m, 30).ToArray();
            decimal?[] atr = Enumerable.Repeat((decimal?)1.0m, 30).ToArray();

            rsi[19] = 28; rsi[20] = 28; rsi[21] = 32;   // RSI 买点 @21

            var signals = SignalEngine.Generate(candles, ema20, ema50, rsi, dif, dea, atr,
                cooldownBars: 6,
                regimeAt: i => new SignalRegime(Adx: 27.5m, AtrPct: 0.012m, BandwidthPct: 0.045m));

            Assert.Equal(1, signals.Count);
            Assert.Equal(27.5m, signals[0].Adx!.Value, 4);
            Assert.Equal(0.012m, signals[0].AtrPct!.Value, 6);
            Assert.Equal(0.045m, signals[0].BandwidthPct!.Value, 6);
        });

        t.Case("信号_v3_结构确认_触及历史支撑并收回产生买点", () =>
        {
            // 构造：前段在 100 附近形成三次摆动低点（确立位点），随后回踩 100 并有效收回
            const int n = 40;
            var candles = new Candle[n];
            var times = Enumerable.Range(0, n).Select(i => 1_000_000L + i * 3600L).ToArray();
            for (var i = 0; i < n; i++)
                candles[i] = new Candle(times[i], 105, 106, 104, 105, 10);
            // 三个摆动低点（历史结构）：i=5 / 15 / 22，低点均为 100（各需左右各 3 根更高）
            foreach (var idx in new[] { 5, 15, 22 })
            {
                candles[idx] = new Candle(times[idx], 104, 105, 100, 103.5m, 10);
                for (var k = 1; k <= 3; k++)
                {
                    candles[idx - k] = new Candle(times[idx - k], 103, 104, 101.5m, 103, 10);
                    candles[idx + k] = new Candle(times[idx + k], 103, 104, 101.5m, 103, 10);
                }
            }
            // 回踩 100 并有效收回（收阳，收盘高于位点 0.1m×ATR 以上）
            candles[33] = new Candle(times[33], 102.5m, 104, 100.2m, 103.5m, 10);

            decimal?[] atr = Enumerable.Repeat((decimal?)2.0m, n).ToArray();  // 容差 0.5m×ATR = 1.0m，收回幅度 0.2m
            decimal?[] flat = Enumerable.Repeat((decimal?)100.0m, n).ToArray();
            decimal?[] zero = Enumerable.Repeat((decimal?)0.0m, n).ToArray();

            var swings = SupportResistance.FindSwings(candles);
            var signals = SignalEngine.Generate(candles, flat, flat, flat, zero, zero, atr,
                cooldownBars: 6, htfCandles: null, htfInterval: null, swings: swings);

            var structure = signals.Where(s => s.Source == "结构").ToList();
            Assert.True(structure.Count >= 1, "应产生结构确认信号");
            Assert.All(structure, s => Assert.Equal("buy", s.Side));
            Assert.Contains("结构确认", structure[0].Note);
        });

        t.Case("信号_v3_结构信号不使用未来数据", () =>
        {
            // 同一段K线，末尾追加更多K线不应改变较早时间点上的结构信号（无未来函数）
            const int n = 60;
            var candles = new Candle[n];
            for (var i = 0; i < n; i++)
            {
                var mid = 100 + (decimal)Math.Sin(i / 4.0) * 3;
                candles[i] = new Candle(1_000_000L + i * 3600L, mid, mid + 1, mid - 1, mid, 10);
            }
            decimal?[] atr = Enumerable.Repeat((decimal?)2.0m, n).ToArray();
            decimal?[] flat = Enumerable.Repeat((decimal?)100.0m, n).ToArray();
            decimal?[] zero = Enumerable.Repeat((decimal?)0.0m, n).ToArray();

            var full = SignalEngine.Generate(candles, flat, flat, flat, zero, zero, atr,
                cooldownBars: 6, htfCandles: null, htfInterval: null, swings: SupportResistance.FindSwings(candles));
            var truncated = SignalEngine.Generate(candles[..(n - 10)], flat[..(n - 10)], flat[..(n - 10)], flat[..(n - 10)],
                zero[..(n - 10)], zero[..(n - 10)], atr[..(n - 10)],
                cooldownBars: 6, htfCandles: null, htfInterval: null,
                swings: SupportResistance.FindSwings(candles[..(n - 10)]));

            var fullEarly = full.Where(s => s.Source == "结构" && s.Time <= candles[n - 11].Time).Select(s => (s.Time, s.Side)).ToList();
            var truncatedAll = truncated.Where(s => s.Source == "结构").Select(s => (s.Time, s.Side)).ToList();
            // 截断后（只用历史数据）得到的结构信号应完全覆盖完整数据中"同一时间点"的信号
            Assert.All(fullEarly, s => Assert.True(truncatedAll.Contains(s),
                $"截断数据后缺少时间点 {s.Time} 的结构信号（疑似使用了未来数据）"));
        });

        // ---------- 端到端（合成K线） ----------

        t.Case("分析引擎_端到端_合成上涨K线", () =>
        {
            // 60 根缓慢单边上涨K线 → 做多方向，且各序列长度与K线一致
            var candles = Enumerable.Range(0, 60)
                .Select(i => new Candle(1000 + i * 60L, 100 + i, 101.5m + i, 99.5m + i, 100.5m + i, 10))
                .ToArray();

            var result = AnalysisEngine.Compute(MarketKind.Futures, TradingPair.Parse("BTCUSDT"), "1h", candles);

            Assert.Equal("futures", result.Market);
            Assert.Equal("BTCUSDT", result.Symbol);
            Assert.Equal("BTC", result.BaseAsset);
            Assert.Equal("USDT", result.QuoteAsset);
            Assert.Equal("LONG", result.Trend.Direction);
            Assert.Equal(candles.Length, result.Series["ema20"].Length);
            Assert.Equal(candles.Length, result.Macd.Hist.Length);
            Assert.Null(result.Series["ema200"][0]);    // 前导未定义段为 null
            Assert.NotNull(result.Series["ema20"][19]);
            Assert.All(result.Signals, s => s.StopPrice is not null);
        });
    }
}
