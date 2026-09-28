using Mdk.Api.Domain;
using Mdk.Api.Indicators;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>
/// 纯函数分析引擎：输入K线序列，输出趋势方向、关键点位与买卖点信号（可直接单测，不依赖网络）。
/// </summary>
public static class AnalysisEngine
{
    public static AnalysisResult Compute(MarketKind market, TradingPair pair, string interval, IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(c => c.Close).ToArray();
        var highs = candles.Select(c => c.High).ToArray();
        var lows = candles.Select(c => c.Low).ToArray();

        var ema20 = Ema.Compute(closes, 20);
        var ema50 = Ema.Compute(closes, 50);
        var ema200 = Ema.Compute(closes, 200);
        var rsi = Rsi.Compute(closes, 14);
        var atr = Atr.Compute(highs, lows, closes, 14);
        var dmi = AdxDmi.Compute(highs, lows, closes, 14);
        var macd = Macd.Compute(closes);
        var boll = BollingerBands.Compute(closes, 20, 2.0);

        var trend = TrendAnalyzer.Analyze(candles, ema50, ema200, dmi);
        var levels = SupportResistance.FindLevels(candles, atr);
        var signals = SignalEngine.Generate(candles, ema20, ema50, rsi, macd.Dif, macd.Dea, atr);

        return new AnalysisResult(
            market.ToString().ToLowerInvariant(),
            pair.Symbol,
            pair.BaseAsset,
            pair.QuoteAsset,
            interval,
            candles[^1].Time,
            candles[^1].Close,
            trend,
            levels,
            signals,
            new Dictionary<string, double?[]>(StringComparer.Ordinal)
            {
                ["ema20"] = ToNullable(ema20),
                ["ema50"] = ToNullable(ema50),
                ["ema200"] = ToNullable(ema200),
                ["rsi14"] = ToNullable(rsi),
                ["atr14"] = ToNullable(atr),
                ["bollUpper"] = ToNullable(boll.Upper),
                ["bollMiddle"] = ToNullable(boll.Middle),
                ["bollLower"] = ToNullable(boll.Lower),
            },
            new MacdSeries(ToNullable(macd.Dif), ToNullable(macd.Dea), ToNullable(macd.Hist)));
    }

    private static double?[] ToNullable(double[] values) =>
        Array.ConvertAll(values, v => double.IsNaN(v) ? (double?)null : v);
}

/// <summary>
/// 趋势方向判定（概念一：有了方向才知道做多还是做空）。
/// 多空两侧逐因子打分，score = 多方得分占比（0–100）：
/// ≥60 做多（LONG），≤40 做空（SHORT），其间为观望（RANGE）。
/// </summary>
public static class TrendAnalyzer
{
    public static TrendResult Analyze(
        IReadOnlyList<Candle> candles,
        double[] ema50,
        double[] ema200,
        AdxDmi.AdxDmiResult dmi)
    {
        var price = candles[^1].Close;
        double bull = 0, bear = 0;
        var reasons = new List<string>();

        var e50 = LastValid(ema50);
        var e200 = LastValid(ema200);
        var pdi = LastValid(dmi.PlusDi);
        var mdi = LastValid(dmi.MinusDi);
        var adx = LastValid(dmi.Adx);

        if (e200 is null)
        {
            reasons.Add("K线数量不足，EMA200 长期趋势因子未参与判定");
        }
        else if (price > e200)
        {
            bull += 30;
            reasons.Add("价格位于 EMA200 上方，长期趋势向上");
        }
        else
        {
            bear += 30;
            reasons.Add("价格位于 EMA200 下方，长期趋势向下");
        }

        if (e50 is not null && e200 is not null)
        {
            if (e50 > e200)
            {
                bull += 20;
                reasons.Add("均线多头排列（EMA50 高于 EMA200）");
            }
            else
            {
                bear += 20;
                reasons.Add("均线空头排列（EMA50 低于 EMA200）");
            }
        }

        if (e50 is not null)
        {
            if (price > e50)
            {
                bull += 15;
                reasons.Add("价格位于 EMA50 上方，中期偏多");
            }
            else
            {
                bear += 15;
                reasons.Add("价格位于 EMA50 下方，中期偏空");
            }
        }

        if (pdi is not null && mdi is not null)
        {
            if (pdi > mdi)
            {
                bull += 20;
                reasons.Add($"+DI({pdi:0.#}) 高于 −DI({mdi:0.#})，多方力量占优");
            }
            else
            {
                bear += 20;
                reasons.Add($"−DI({mdi:0.#}) 高于 +DI({pdi:0.#})，空方力量占优");
            }
        }

        var leader = bull >= bear ? "多头" : "空头";
        if (adx is >= 25)
        {
            if (bull > bear) bull += 15;
            else bear += 15;
            reasons.Add($"ADX({adx:0.#}) ≥ 25，当前为强趋势，{leader}方向可靠性高");
        }
        else if (adx is not null && adx < 20)
        {
            reasons.Add($"ADX({adx:0.#}) < 20，趋势强度弱，方向可靠性降低，建议观望为主");
        }

        if (bull + bear == 0)
            return new TrendResult("RANGE", 50, ["有效因子不足，无法判定方向"]);

        var score = (int)Math.Round(100 * bull / (bull + bear));
        var direction = score >= 60 ? "LONG" : score <= 40 ? "SHORT" : "RANGE";
        return new TrendResult(direction, score, reasons);
    }

    private static double? LastValid(double[] values)
    {
        for (var i = values.Length - 1; i >= 0; i--)
            if (!double.IsNaN(values[i]))
                return values[i];
        return null;
    }
}

/// <summary>
/// 关键价格点位（概念二：支撑位与阻力位）。
/// 摆动高低点（±lookback 根K线的分形极值）按 0.5×ATR 容差聚类，
/// 触碰次数即强度；按现价上下分为阻力/支撑，各取最近的 maxPerSide 个。
/// </summary>
public static class SupportResistance
{
    public static IReadOnlyList<PriceLevel> FindLevels(
        IReadOnlyList<Candle> candles,
        double[] atr,
        int lookback = 3,
        int maxPerSide = 5)
    {
        var last = candles[^1];
        var tolerance = 0.5 * (LastValid(atr) ?? last.Close * 0.01);

        var swings = new List<double>();
        for (var i = lookback; i < candles.Count - lookback; i++)
        {
            var isHigh = true;
            var isLow = true;
            for (var k = 1; k <= lookback; k++)
            {
                if (candles[i].High <= candles[i - k].High || candles[i].High <= candles[i + k].High) isHigh = false;
                if (candles[i].Low >= candles[i - k].Low || candles[i].Low >= candles[i + k].Low) isLow = false;
                if (!isHigh && !isLow) break;
            }
            if (isHigh) swings.Add(candles[i].High);
            if (isLow) swings.Add(candles[i].Low);
        }

        // 按时间序聚类：价差在容差内的摆动点视为同一位点，均值作价位，次数作强度
        var clustered = new List<(double Sum, int Count)>();
        foreach (var price in swings)
        {
            var merged = false;
            for (var i = 0; i < clustered.Count; i++)
            {
                var avg = clustered[i].Sum / clustered[i].Count;
                if (Math.Abs(price - avg) > tolerance) continue;
                clustered[i] = (clustered[i].Sum + price, clustered[i].Count + 1);
                merged = true;
                break;
            }
            if (!merged) clustered.Add((price, 1));
        }

        var levels = clustered
            .Select(c => (Price: c.Sum / c.Count, Strength: c.Count))
            .ToList();

        var supports = levels
            .Where(l => l.Price < last.Close)
            .OrderByDescending(l => l.Price)
            .Take(maxPerSide)
            .Select(l => new PriceLevel("support", l.Price, l.Strength, Pct(l.Price, last.Close)))
            .ToList();

        var resistances = levels
            .Where(l => l.Price >= last.Close)
            .OrderBy(l => l.Price)
            .Take(maxPerSide)
            .Select(l => new PriceLevel("resistance", l.Price, l.Strength, Pct(l.Price, last.Close)))
            .ToList();

        return resistances.Concat(supports).ToList();
    }

    private static double Pct(double level, double price) => Math.Round((level - price) / price * 100, 2);

    private static double? LastValid(double[] values)
    {
        for (var i = values.Length - 1; i >= 0; i--)
            if (!double.IsNaN(values[i]))
                return values[i];
        return null;
    }
}

/// <summary>
/// 买卖点信号（概念三：买卖点相关指标）。
/// 由已启用指标的交叉/反转规则自动打点，每个信号附 2×ATR 的止损参考价。
/// </summary>
public static class SignalEngine
{
    public static IReadOnlyList<TradeSignal> Generate(
        IReadOnlyList<Candle> candles,
        double[] ema20,
        double[] ema50,
        double[] rsi,
        double[] dif,
        double[] dea,
        double[] atr)
    {
        var signals = new List<TradeSignal>();

        void Add(int i, string side, string source, string note)
        {
            var price = candles[i].Close;
            double? stop = null;
            if (i < atr.Length && !double.IsNaN(atr[i]))
                stop = side == "buy" ? price - 2 * atr[i] : price + 2 * atr[i];
            signals.Add(new TradeSignal(candles[i].Time, side, source, price, note, stop));
        }

        for (var i = 1; i < candles.Count; i++)
        {
            // EMA20 × EMA50 交叉：中期动能转换
            if (Ok(ema20, i) && Ok(ema50, i) && Ok(ema20, i - 1) && Ok(ema50, i - 1))
            {
                if (ema20[i - 1] <= ema50[i - 1] && ema20[i] > ema50[i])
                    Add(i, "buy", "EMA", "EMA20 上穿 EMA50（金叉），中期动能转多");
                else if (ema20[i - 1] >= ema50[i - 1] && ema20[i] < ema50[i])
                    Add(i, "sell", "EMA", "EMA20 下穿 EMA50（死叉），中期动能转空");
            }

            // RSI 超卖/超买反转（Wilder 平滑）
            if (Ok(rsi, i) && Ok(rsi, i - 1))
            {
                if (rsi[i - 1] <= 30 && rsi[i] > 30)
                    Add(i, "buy", "RSI", $"RSI({rsi[i - 1]:0.#}) 超卖后回升上穿 30，短线反弹信号");
                else if (rsi[i - 1] >= 70 && rsi[i] < 70)
                    Add(i, "sell", "RSI", $"RSI({rsi[i - 1]:0.#}) 超买后回落下穿 70，短线回调信号");
            }

            // MACD 金叉/死叉
            if (Ok(dif, i) && Ok(dea, i) && Ok(dif, i - 1) && Ok(dea, i - 1))
            {
                if (dif[i - 1] <= dea[i - 1] && dif[i] > dea[i])
                    Add(i, "buy", "MACD", "MACD 金叉（DIF 上穿 DEA），动量转多");
                else if (dif[i - 1] >= dea[i - 1] && dif[i] < dea[i])
                    Add(i, "sell", "MACD", "MACD 死叉（DIF 下穿 DEA），动量转空");
            }
        }
        return signals;
    }

    private static bool Ok(double[] values, int i) =>
        i < values.Length && !double.IsNaN(values[i]);
}
