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
            // 锚点关掉：本用例只验摆动极值的聚类与分侧（客观锚点另有用例）
            var levels = PriceLevels.Analyze(candles, new PriceLevelOptions { Anchors = false });
            var lastClose = candles[^1].Close; // 96

            var resistance = levels.Where(l => l.Kind == "resistance").ToList();
            var support = levels.Where(l => l.Kind == "support").ToList();

            var top = Assert.Single(resistance);
            Assert.Equal(110.5m, top.Price, 1);
            Assert.Equal(4, top.Touches); // 峰出现 4 次（i=3,15,27,39）
            Assert.True(top.Retested, "峰被触碰 4 次：应为已反复测试的位点");

            Assert.Contains(support, s => Math.Abs(s.Price - 89.5m) < 1);
            Assert.All(support, s => s.Price < lastClose);
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
