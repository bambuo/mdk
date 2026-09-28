using Mdk.Api.Domain;
using Mdk.Api.Indicators;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>
/// 纯函数分析引擎：输入K线序列，输出趋势方向、关键点位与买卖点信号（可直接单测，不依赖网络）。
/// </summary>
public static class AnalysisEngine
{
    public static AnalysisResult Compute(
        MarketKind market,
        TradingPair pair,
        string interval,
        IReadOnlyList<Candle> candles,
        SignalOptions? signalOptions = null,
        IReadOnlyList<Candle>? htfCandles = null,
        string? htfInterval = null)
    {
        var so = signalOptions ?? SignalOptions.Default;
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

        // 信号用快慢线：1h 可选提速参数（10/30），其余周期用 20/50（依据 PLAN §0.4 实测）
        var useFast = so.UseFastEmaOn1h && interval == "1h";
        var sigFast = useFast ? Ema.Compute(closes, so.FastEmaPeriod) : ema20;
        var sigSlow = useFast ? Ema.Compute(closes, so.FastEmaSlowPeriod) : ema50;

        var trend = TrendAnalyzer.Analyze(candles, ema50, ema200, dmi);
        var levels = SupportResistance.FindLevels(candles, atr);
        var swings = SupportResistance.FindSwings(candles);

        // 信号发生时的市场状态（ADX 强度 / 波动率 / 带宽），用于事后状态依赖统计
        SignalRegime RegimeAt(int i)
        {
            var adx = i < dmi.Adx.Length && !double.IsNaN(dmi.Adx[i]) ? dmi.Adx[i] : (double?)null;
            var close = candles[i].Close;
            var atrPct = i < atr.Length && !double.IsNaN(atr[i]) && close > 0 ? atr[i] / close : (double?)null;
            var mid = boll.Middle[i];
            var bandwidth = !double.IsNaN(mid) && mid > 0 && !double.IsNaN(boll.Upper[i]) && !double.IsNaN(boll.Lower[i])
                ? (boll.Upper[i] - boll.Lower[i]) / mid
                : (double?)null;
            return new SignalRegime(adx, atrPct, bandwidth);
        }

        var signals = SignalEngine.Generate(candles, sigFast, sigSlow, rsi, macd.Dif, macd.Dea, atr,
            so.CooldownBars, htfCandles, htfInterval, swings, RegimeAt, so.IncludeStructureSignals);

        var series = new Dictionary<string, double?[]>(StringComparer.Ordinal)
        {
            ["ema20"] = ToNullable(ema20),
            ["ema50"] = ToNullable(ema50),
            ["ema200"] = ToNullable(ema200),
            ["rsi14"] = ToNullable(rsi),
            ["atr14"] = ToNullable(atr),
            ["bollUpper"] = ToNullable(boll.Upper),
            ["bollMiddle"] = ToNullable(boll.Middle),
            ["bollLower"] = ToNullable(boll.Lower),
        };
        if (useFast)
        {
            series["emaSigFast"] = ToNullable(sigFast);
            series["emaSigSlow"] = ToNullable(sigSlow);
        }

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
            series,
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
    /// <summary>摆动点检测（±lookback 根K线的分形极值），供位点聚类与结构信号共用。</summary>
    public static IReadOnlyList<SwingPoint> FindSwings(IReadOnlyList<Candle> candles, int lookback = 3)
    {
        var swings = new List<SwingPoint>();
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
            if (isHigh) swings.Add(new SwingPoint(i, candles[i].High, true));
            if (isLow) swings.Add(new SwingPoint(i, candles[i].Low, false));
        }
        return swings;
    }

    public static IReadOnlyList<PriceLevel> FindLevels(
        IReadOnlyList<Candle> candles,
        double[] atr,
        int lookback = 3,
        int maxPerSide = 5)
    {
        var last = candles[^1];
        var tolerance = 0.5 * (LastValid(atr) ?? last.Close * 0.01);

        var swings = FindSwings(candles, lookback).Select(s => s.Price).ToList();

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
/// v2/v3 可用性改造（2026-09-28，依据事后审计数据，见 PLAN §0.4/§0.5）：
/// ① 收盘确认分级：只把「已收盘K线」上的交叉记为确认信号（不可撤销）；
///    未收盘K线上的交叉记为盘中预警（IsConfirmed=false，可能消失）——消除重绘，兼顾即时性。
/// ② 冷却去重：同源同向在 cooldownBars 根内重复触发不重复报。
/// ③ 多周期共振标记：TrendAligned；注意：生产样本中该过滤证据矛盾（见 PLAN §0.5），仅作标注不作依据。
/// ④ 市场状态随信号记录（ADX/ATR%/带宽），用于事后判断信号的状态依赖性。
/// ⑤ 实验性「结构确认」信号源：价格触及由**已确认历史摆动点**构成的支撑/阻力并收回，
///    不使用未来数据（即时性优于均线交叉；按关键位反应入场，止损更抗噪）。
/// </summary>
public static class SignalEngine
{
    /// <summary>结构信号：形成有效位点所需的最少摆动点触碰次数（3 次以上才算确立的位点，抑制噪音触发）。</summary>
    private const int SrMinTouches = 3;

    /// <summary>结构信号：收盘有效收回位点所需的额外幅度（0.1×ATR，避免刚好压线的假收回）。</summary>
    private const double SrReclaimAtr = 0.1;

    public static IReadOnlyList<TradeSignal> Generate(
        IReadOnlyList<Candle> candles,
        double[] emaFast,
        double[] emaSlow,
        double[] rsi,
        double[] dif,
        double[] dea,
        double[] atr,
        int cooldownBars = 6,
        IReadOnlyList<Candle>? htfCandles = null,
        string? htfInterval = null,
        IReadOnlyList<SwingPoint>? swings = null,
        Func<int, SignalRegime>? regimeAt = null,
        bool includeStructure = true)
    {
        var signals = new List<TradeSignal>();

        // 高周期趋势：对每个信号时间，取「已收盘」的最新高周期K线，比较其收盘价与高周期 EMA50
        double[]? htfEma50 = null;
        long[]? htfTimes = null;
        long htfSeconds = 0;
        double[]? htfCloses = null;
        if (htfCandles is { Count: > 50 } htf && !string.IsNullOrEmpty(htfInterval))
        {
            htfCloses = htf.Select(c => c.Close).ToArray();
            htfEma50 = Ema.Compute(htfCloses, 50);
            htfTimes = htf.Select(c => c.Time).ToArray();
            htfSeconds = MarketIntervals.IntervalSeconds(htfInterval);
        }

        bool? TrendAligned(long signalTime, string side)
        {
            if (htfEma50 == null || htfTimes == null || htfSeconds == 0 || htfCloses == null) return null;
            // 信号时刻 t：高周期K线 ht 需满足 ht + htfSeconds <= t 才算已收盘
            var idx = -1;
            for (var j = htfTimes.Length - 1; j >= 0; j--)
            {
                if (htfTimes[j] + htfSeconds <= signalTime) { idx = j; break; }
            }
            if (idx < 0 || double.IsNaN(htfEma50[idx])) return null;
            var above = htfCloses[idx] > htfEma50[idx];
            return side == "buy" ? above : !above;
        }

        void Add(int i, string side, string source, string note, bool confirmed)
        {
            var price = candles[i].Close;
            double? stop = null;
            if (i < atr.Length && !double.IsNaN(atr[i]))
                stop = side == "buy" ? price - 2 * atr[i] : price + 2 * atr[i];
            var regime = regimeAt?.Invoke(i) ?? default;
            signals.Add(new TradeSignal(
                Time: candles[i].Time,
                Side: side,
                Source: source,
                Price: price,
                Note: note,
                StopPrice: stop,
                IsConfirmed: confirmed,
                TrendAligned: TrendAligned(candles[i].Time, side),
                Adx: regime.Adx,
                AtrPct: regime.AtrPct,
                BandwidthPct: regime.BandwidthPct));
        }

        // 确认信号：交叉发生在已收盘K线 i（i ≤ n−2）；同源同向冷却去重
        var lastKept = new Dictionary<string, int>();
        for (var i = 1; i <= candles.Count - 2; i++)
        {
            var i1 = i - 1;

            if (Ok(emaFast, i) && Ok(emaSlow, i) && Ok(emaFast, i1) && Ok(emaSlow, i1))
            {
                if (emaFast[i1] <= emaSlow[i1] && emaFast[i] > emaSlow[i]) TryKeep(i, "buy", "EMA", "EMA 快线上穿慢线（金叉），中期动能转多", cooldownBars, lastKept, Add);
                else if (emaFast[i1] >= emaSlow[i1] && emaFast[i] < emaSlow[i]) TryKeep(i, "sell", "EMA", "EMA 快线下穿慢线（死叉），中期动能转空", cooldownBars, lastKept, Add);
            }

            if (Ok(rsi, i) && Ok(rsi, i1))
            {
                if (rsi[i1] <= 30 && rsi[i] > 30) TryKeep(i, "buy", "RSI", $"RSI({rsi[i1]:0.#}) 超卖后回升上穿 30，短线反弹信号", cooldownBars, lastKept, Add);
                else if (rsi[i1] >= 70 && rsi[i] < 70) TryKeep(i, "sell", "RSI", $"RSI({rsi[i1]:0.#}) 超买后回落下穿 70，短线回调信号", cooldownBars, lastKept, Add);
            }

            if (Ok(dif, i) && Ok(dea, i) && Ok(dif, i1) && Ok(dea, i1))
            {
                if (dif[i1] <= dea[i1] && dif[i] > dea[i]) TryKeep(i, "buy", "MACD", "MACD 金叉（DIF 上穿 DEA），动量转多", cooldownBars, lastKept, Add);
                else if (dif[i1] >= dea[i1] && dif[i] < dea[i]) TryKeep(i, "sell", "MACD", "MACD 死叉（DIF 下穿 DEA），动量转空", cooldownBars, lastKept, Add);
            }

            // 结构确认（实验）：触及已确认的历史支撑/阻力并收回
            if (includeStructure && swings is { Count: > 0 } && Ok(atr, i))
            {
                if (TryStructure(i, candles, swings, atr) is { } hit)
                    TryKeep(i, hit.Side, "结构", hit.Note, cooldownBars, lastKept, Add);
            }
        }

        // 盘中预警：仅评估最后一根（未收盘）K线；随 WS 收盘重算自动转正或消失
        var last = candles.Count - 1;
        if (last >= 1)
        {
            var p = last;
            var p1 = last - 1;
            if (Ok(emaFast, p) && Ok(emaSlow, p) && Ok(emaFast, p1) && Ok(emaSlow, p1))
            {
                if (emaFast[p1] <= emaSlow[p1] && emaFast[p] > emaSlow[p]) Add(p, "buy", "EMA", "EMA 快线上穿慢线（盘中预警，未确认）", confirmed: false);
                else if (emaFast[p1] >= emaSlow[p1] && emaFast[p] < emaSlow[p]) Add(p, "sell", "EMA", "EMA 快线下穿慢线（盘中预警，未确认）", confirmed: false);
            }
            if (Ok(rsi, p) && Ok(rsi, p1))
            {
                if (rsi[p1] <= 30 && rsi[p] > 30) Add(p, "buy", "RSI", $"RSI({rsi[p1]:0.#}) 超卖后回升上穿 30（盘中预警，未确认）", confirmed: false);
                else if (rsi[p1] >= 70 && rsi[p] < 70) Add(p, "sell", "RSI", $"RSI({rsi[p1]:0.#}) 超买后回落下穿 70（盘中预警，未确认）", confirmed: false);
            }
            if (Ok(dif, p) && Ok(dea, p) && Ok(dif, p1) && Ok(dea, p1))
            {
                if (dif[p1] <= dea[p1] && dif[p] > dea[p]) Add(p, "buy", "MACD", "MACD 金叉（DIF 上穿 DEA）（盘中预警，未确认）", confirmed: false);
                else if (dif[p1] >= dea[p1] && dif[p] < dea[p]) Add(p, "sell", "MACD", "MACD 死叉（DIF 下穿 DEA）（盘中预警，未确认）", confirmed: false);
            }
        }
        return signals;
    }

    /// <summary>
    /// 结构确认：在 bar i 处，判断价格是否触及「只由 i 之前已确认的摆动点」构成的有效位点并收回。
    /// 只用历史摆动点（Index ≤ i − lookback），不使用未来数据。
    /// </summary>
    private static (string Side, string Note)? TryStructure(
        int i, IReadOnlyList<Candle> candles, IReadOnlyList<SwingPoint> swings, double[] atr)
    {
        var bar = candles[i];
        var atrVal = atr[i];
        if (atrVal <= 0) return null;
        var tol = 0.5 * atrVal;
        var reclaim = SrReclaimAtr * atrVal;

        // 支撑（用历史摆动低点）：最低价进入位点容差带、收盘有效收回位点上方、且收阳
        var bestSupport = FindLevel(swings, i, isHigh: false, bar.Low, tol);
        if (bestSupport is { } support && bar.Low <= support.Price + 0.25 * atrVal && bar.Close > support.Price + reclaim && bar.Close > bar.Open)
            return ("buy", $"触及支撑 {support.Price:0.##}（{support.Touches} 次触碰）收回，结构确认（实验）");

        // 阻力（用历史摆动高点）：最高价进入位点容差带、收盘有效回落到位点下方、且收阴
        var bestResistance = FindLevel(swings, i, isHigh: true, bar.High, tol);
        if (bestResistance is { } resistance && bar.High >= resistance.Price - 0.25 * atrVal && bar.Close < resistance.Price - reclaim && bar.Close < bar.Open)
            return ("sell", $"触及阻力 {resistance.Price:0.##}（{resistance.Touches} 次触碰）回落，结构确认（实验）");

        return null;
    }

    /// <summary>在已确认的历史摆动点中，找出离 price 最近且触碰次数达标的位点。</summary>
    private static (double Price, int Touches)? FindLevel(
        IReadOnlyList<SwingPoint> swings, int barIndex, bool isHigh, double price, double tol)
    {
        // 已确认的摆动点：至少滞后 barIndex 若干根（此处用全局 lookback=3 的共识：Index ≤ barIndex − 3）
        var minIndex = barIndex - 3;
        var candidates = swings.Where(s => s.IsHigh == isHigh && s.Index <= minIndex).ToList();
        if (candidates.Count == 0) return null;

        (double Price, int Touches)? best = null;
        foreach (var c in candidates)
        {
            if (Math.Abs(c.Price - price) > tol) continue;
            var touches = candidates.Count(s => Math.Abs(s.Price - c.Price) <= tol);
            if (touches < SrMinTouches) continue;
            if (best is null || touches > best.Value.Touches)
                best = (c.Price, touches);
        }
        return best;
    }

    private static void TryKeep(
        int i, string side, string source, string note, int cooldownBars,
        Dictionary<string, int> lastKept, Action<int, string, string, string, bool> add)
    {
        var key = $"{source}|{side}";
        if (lastKept.TryGetValue(key, out var prev) && i - prev < cooldownBars) return; // 冷却期内，抑制重复
        lastKept[key] = i;
        add(i, side, source, note, true);
    }

    private static bool Ok(double[] values, int i) =>
        i < values.Length && !double.IsNaN(values[i]);
}
