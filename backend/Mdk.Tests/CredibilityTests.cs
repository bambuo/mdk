using Mdk.Api.Analysis;
using Mdk.Api.Domain;

namespace Mdk.Tests;

/// <summary>
/// 可信度口径：锚定币判定（领域规则）、经验胜率与 Wilson 区间、样本不足的如实标记、
/// 中位超额与典型风险单位（抗极值口径）。
/// </summary>
public static class CredibilityTests
{
    public static void Register(TestKit t)
    {
        // ─────────────────── 锚定币（领域规则） ───────────────────

        t.Case("锚定币_稳定币与法币识别", () =>
        {
            foreach (var asset in new[] { "USDT", "USDC", "FDUSD", "TUSD", "USDP", "BUSD", "DAI", "USD1", "USDE", "XUSD", "PYUSD", "usdc" })
                Assert.Equal(true, PeggedAssets.IsPegged(asset));
            foreach (var asset in new[] { "EUR", "GBP", "TRY", "BRL", "JPY" })
                Assert.Equal(true, PeggedAssets.IsPegged(asset));
        });

        t.Case("锚定币_普通币种不误判", () =>
        {
            foreach (var asset in new[] { "BTC", "ETH", "SOL", "USUAL", "USELESS", "DOGE", "XRP", "ADA", "TRX", "USDTB" })
                Assert.Equal(false, PeggedAssets.IsPegged(asset));
        });

        t.Case("锚定币_交易对值对象直接可判", () =>
        {
            Assert.Equal(true, TradingPair.Parse("USDCUSDT").IsPegged);
            Assert.Equal(true, TradingPair.Parse("USD1USDT").IsPegged);
            Assert.Equal(false, TradingPair.Parse("BTCUSDT").IsPegged);
            Assert.Equal(false, TradingPair.Parse("USUALUSDT").IsPegged);
        });

        // ─────────────────── 信号的风险口径 ───────────────────

        t.Case("风险口径_入场滞后与止损距离与滞后占比", () =>
        {
            // 3买：记在 83752.01，结构参考点 83664.01（中枢上沿），止损 83898.31
            var sig = new TradeSignal(
                Time: 1, Side: "sell", Source: "缠论", Price: 83752.01m, Note: "", StopPrice: 83898.31409564m,
                IsConfirmed: true, TrendAligned: null, ReferencePrice: 83664.01m, Kind: "3卖");
            Assert.Equal(0.00174687m, sig.RiskPct!.Value, 8);
            Assert.Equal(0.00105072m, sig.EntryLagPct!.Value, 8);
            Assert.Equal(0.60149m, sig.LagShare!.Value, 5);
        });

        t.Case("风险口径_缺结构参考价时只给风险单位", () =>
        {
            var sig = new TradeSignal(
                Time: 1, Side: "buy", Source: "EMA", Price: 100m, Note: "", StopPrice: 98m,
                IsConfirmed: true, TrendAligned: null);
            Assert.Equal(0.02m, sig.RiskPct!.Value, 6);
            Assert.Equal(true, sig.EntryLagPct is null);
            Assert.Equal(true, sig.LagShare is null);
        });

        t.Case("风险口径_无止损时不给风险相关指标", () =>
        {
            var sig = new TradeSignal(
                Time: 1, Side: "buy", Source: "RSI", Price: 100m, Note: "", StopPrice: null,
                IsConfirmed: true, TrendAligned: null, ReferencePrice: 95m);
            Assert.Equal(true, sig.RiskPct is null);
            Assert.Equal(true, sig.LagShare is null);
            Assert.Equal(0.05m, sig.EntryLagPct!.Value, 6);   // 滞后仍可算（有参考价即可）
        });

        // ─────────────────── 可信度统计 ───────────────────

        t.Case("可信度_样本不足时不给胜率", () =>
        {
            var items = Entries(wins: 5, losses: 5, symbol: "BTCUSDT");
            var bucket = SignalCredibilityRules.Build("3买", "1h", items);
            Assert.Equal(false, bucket.Sufficient);
            Assert.Equal(10, bucket.NEpisodes);
        });

        t.Case("可信度_胜率与Wilson区间_样本充足", () =>
        {
            // 40 波、24 胜 → 60%；区间应包住点估计且落在 [0,1] 内
            var items = Entries(wins: 24, losses: 16, symbol: "BTCUSDT", spreadOverSymbols: true);
            var bucket = SignalCredibilityRules.Build("3买", "1h", items);
            Assert.Equal(true, bucket.Sufficient);
            Assert.Equal(40, bucket.NEpisodes);
            Assert.Equal(0.6m, bucket.WinRate, 6);
            Assert.Equal(true, bucket.WinRateLow < bucket.WinRate && bucket.WinRate < bucket.WinRateHigh);
            Assert.Equal(true, bucket.WinRateLow >= 0m && bucket.WinRateHigh <= 1m);
        });

        t.Case("可信度_Wilson区间_随样本收窄且不越界", () =>
        {
            var small = SignalCredibilityRules.Wilson(6, 10);
            var large = SignalCredibilityRules.Wilson(600, 1000);
            Assert.Equal(true, large.High - large.Low < small.High - small.Low);
            var extreme = SignalCredibilityRules.Wilson(0, 5);
            Assert.Equal(true, extreme.Low >= 0m && extreme.High <= 1m);
            Assert.Equal(true, extreme.High < 1m);      // 小样本全败也不给"必然失败"
            var empty = SignalCredibilityRules.Wilson(0, 0);
            Assert.Equal(0m, empty.Low);
            Assert.Equal(1m, empty.High);
        });

        t.Case("可信度_中位数口径_不被极值带偏", () =>
        {
            // 9 条微亏 + 1 条巨赢：均值会显示为正，中位数必须为负（避免"平均超额"误导）
            var items = new List<SignalEntry>();
            for (var i = 0; i < 10; i++)
            {
                var bigWin = i == 9;
                items.Add(Entry(
                    symbol: "BTCUSDT", time: 1_000_000 + i * 30 * 3600L,
                    ret: bigWin ? 0.40m : -0.001m,
                    excess: bigWin ? 0.40m : -0.001m,
                    price: 100m, stop: 98m));
            }
            var bucket = SignalCredibilityRules.Build("3买", "1h", items);
            Assert.Equal(true, bucket.MedianExcess < 0m);
            Assert.Equal(0.02m, bucket.MedianRiskPct, 6);   // |100−98|/100
        });

        // ─────────────────── 实盘晋升判据（预注册阈值锁定） ───────────────────

        t.Case("实盘判据_实时落库口径_回算不算数", () =>
        {
            var sec = MarketIntervals.IntervalSeconds("1h");
            var realtime = Entry("BTCUSDT", 1_000_000, 0.01m, 0.005m, 100m, 98m);
            realtime.RecordedAt = 1_000_000 + sec;                       // 1 根内 → 实时
            var backfill = Entry("BTCUSDT", 2_000_000, 0.01m, 0.005m, 100m, 98m);
            backfill.RecordedAt = 2_000_000 + (long)(2.5m * sec);        // 2.5 根后 → 回算
            Assert.Equal(true, SignalQualityRules.IsRealtimeRecorded(realtime));
            Assert.Equal(false, SignalQualityRules.IsRealtimeRecorded(backfill));
        });

        t.Case("实盘判据_实时波次不足_不予晋升", () =>
        {
            var reps = RealtimeEpisodes(waves: 60);
            var (ok, reason) = SignalQualityRules.TradableGate(reps);
            Assert.Equal(false, ok);
            Assert.Contains("实时独立波次 60 < 100", reason);
        });

        t.Case("实盘判据_全部达标_才予晋升", () =>
        {
            var reps = RealtimeEpisodes(waves: 120);
            var (ok, reason) = SignalQualityRules.TradableGate(reps);
            Assert.Equal(true, ok);
            Assert.Contains("波次 120", reason);
            Assert.Contains("分半稳定", reason);
        });

        t.Case("实盘判据_时间分半不稳定_不予晋升", () =>
        {
            // 前半大正 / 后半微负：整体 t 仍 ≥2，但分半检查必须拦下（样本外已失效）
            var reps = RealtimeEpisodes(waves: 120, firstHalfExcess: 0.02m, secondHalfExcess: -0.0001m);
            var (ok, reason) = SignalQualityRules.TradableGate(reps);
            Assert.Equal(false, ok);
            Assert.Contains("时间分半不稳定", reason);
        });

        t.Case("实盘判据_MAE超过1R_不予晋升", () =>
        {
            var reps = RealtimeEpisodes(waves: 120, maeR: 1.5m);
            var (ok, reason) = SignalQualityRules.TradableGate(reps);
            Assert.Equal(false, ok);
            Assert.Contains("MAE 中位 1.50R > 1R", reason);
        });

        t.Case("可信度_扣费后为正按波次口径统计", () =>
        {
            var items = new List<SignalEntry>();
            for (var i = 0; i < 40; i++)
                items.Add(Entry("AAAUSDT", 1_000_000 + i * 30 * 3600L, ret: 0.01m, excess: 0.005m,
                    price: 100m, stop: 98m, netPositive: i % 4 != 0));   // 3/4 扣费后为正
            var bucket = SignalCredibilityRules.Build("3买", "1h", items);
            Assert.Equal(0.75m, bucket.NetPositiveRate, 6);
        });
    }

    private static SignalEntry Entry(
        string symbol, long time, decimal ret, decimal excess, decimal price, decimal stop,
        bool netPositive = true) => new()
    {
        Market = MarketKind.Spot,
        Pair = TradingPair.Parse(symbol),
        Interval = "1h",
        Source = "缠论",
        Kind = "3买",
        Side = "buy",
        Time = time,
        Price = price,
        StopPrice = stop,
        ReferencePrice = price,
        IsConfirmed = true,
        RecordedAt = time,
        Outcome = new SignalOutcome
        {
            Status = "ok", Ret = ret, Excess = excess, Mfe = ret, Mae = -0.01m,
            StopHit = false, NetPositive = netPositive, EvaluatedAt = time,
        },
    };

    /// <summary>
    /// 构造实时落库的独立波次样本：跨 3 个标的、间隔 30 根、前后两半的超额可分别控制、MAE 可控。
    /// </summary>
    private static List<SignalEntry> RealtimeEpisodes(
        int waves, decimal firstHalfExcess = 0.004m, decimal secondHalfExcess = 0.006m,
        decimal netPositiveRate = 0.6m, decimal maeR = 0.5m)
    {
        var items = new List<SignalEntry>();
        var symbols = new[] { "AAAUSDT", "BBBUSDT", "CCCUSDT" };
        var sec = MarketIntervals.IntervalSeconds("1h");
        for (var i = 0; i < waves; i++)
        {
            var e = Entry(
                symbol: symbols[i % symbols.Length], time: 1_000_000 + i * 30 * 3600L,
                ret: 0.01m, excess: i < waves / 2 ? firstHalfExcess : secondHalfExcess,
                price: 100m, stop: 98m, netPositive: i < waves * netPositiveRate);
            e.RecordedAt = e.Time + sec;                       // 1 根内落库 = 实时
            e.Outcome!.Mae = -0.02m * maeR;                    // dir=1：|MAE| = riskPct × maeR = 0.02m × maeR
            items.Add(e);
        }
        return items;
    }

    /// <summary>构造 n 条样本：同币种间隔 30 根（各自独立成波）；spreadOverSymbols 时轮流换标的。</summary>
    private static List<SignalEntry> Entries(int wins, int losses, string symbol, bool spreadOverSymbols = false)
    {
        var items = new List<SignalEntry>();
        var total = wins + losses;
        var symbols = new[] { symbol, "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT" };
        for (var i = 0; i < total; i++)
        {
            var win = i < wins;
            items.Add(Entry(
                symbol: spreadOverSymbols ? symbols[i % symbols.Length] : symbol,
                time: 1_000_000 + i * 30 * 3600L,
                ret: win ? 0.01m + (i % 5) * 0.001m : -0.01m,
                excess: win ? 0.005m + (i % 7) * 0.001m : -0.008m,
                price: 100m, stop: 98m));
        }
        return items;
    }
}
