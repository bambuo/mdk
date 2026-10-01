using Mdk.Api.Domain;
using Mdk.Api.Indicators;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>
/// 纯函数分析引擎：输入K线序列，输出**缠论买卖点信号**与指标序列（可直接单测，不依赖网络）。
/// 2026-09-30 精简为纯缠论：趋势方向/关键点位仅作展示，EMA/RSI/MACD 仅作图表指标。
/// </summary>
public static class AnalysisEngine
{
    public static AnalysisResult Compute(
        MarketKind market,
        TradingPair pair,
        string interval,
        IReadOnlyList<Candle> candles,
        IReadOnlyList<Candle>? htfCandles = null,
        string? htfInterval = null,
        Chan.ChanOptions? chanOptions = null,
        IReadOnlyList<Candle>? chanCandles = null,
        IReadOnlyList<Candle>? subLevelCandles = null,
        string? subLevelInterval = null,
        IReadOnlyList<Candle>? lowerLevelCandles = null,
        string? lowerLevelInterval = null)
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
        var boll = BollingerBands.Compute(closes, 20, 2.0m);

        var trend = TrendAnalyzer.Analyze(candles, ema50, ema200, dmi);
        var levels = SupportResistance.FindLevels(candles, atr);

        // 信号发生时的市场状态（ADX 强度 / 波动率 / 带宽），用于事后状态依赖统计
        SignalRegime RegimeAt(int i)
        {
            var adx = i < dmi.Adx.Length && dmi.Adx[i] is not null ? dmi.Adx[i] : (decimal?)null;
            var close = candles[i].Close;
            var atrPct = i < atr.Length && atr[i] is not null && close > 0 ? atr[i] / close : null;
            var mid = boll.Middle[i];
            var bandwidth = mid is not null && mid > 0 && boll.Upper[i] is not null && boll.Lower[i] is not null
                ? (boll.Upper[i] - boll.Lower[i]) / mid
                : (decimal?)null;
            return new SignalRegime(adx, atrPct, bandwidth);
        }

        // 缠论：结构（分型/笔/中枢）+ 买卖点。
        // 在"显示窗口 + 固定历史缓冲"的扩展窗口上计算（末段与显示窗口对齐）：中枢划分依赖先行历史，
        // 若只用显示窗口，紧贴窗口起点的中枢会随窗口滑动而改变；配合 Chan:WarmupBars 暖机隔离，
        // 使发出的信号与窗口起点无关（可复现）。
        var chanOpt = chanOptions ?? Chan.ChanOptions.Default;
        var chanWindow = chanCandles is { Count: > 0 } ext ? ext : candles;
        var chanOffset = chanWindow.Count - candles.Count;
        Chan.ChanResult? chanResult = null;
        if (chanOpt.Enabled)
        {
            // 关键：MACD/ATR 必须始终在"内部固定窗口"上计算——否则同一结构在不同显示窗口下
            // 会因指标起算点不同而得出不同的止损与可交易性判定，破坏复现性（2026-09-29 复测发现）。
            decimal?[] chanHist, chanAtr;
            if (ReferenceEquals(chanWindow, candles))
            {
                chanHist = macd.Hist;
                chanAtr = atr;
            }
            else
            {
                var chanCloses = chanWindow.Select(c => c.Close).ToArray();
                chanHist = Macd.Compute(chanCloses).Hist;
                chanAtr = Atr.Compute(chanWindow.Select(c => c.High).ToArray(), chanWindow.Select(c => c.Low).ToArray(),
                    chanCloses, 14);
            }

            var raw = Chan.ChanAnalyzer.Analyze(chanWindow, chanHist, chanAtr, chanOpt, subLevelCandles,
                subLevelInterval);
            chanResult = chanOffset == 0 ? raw : AlignToDisplayWindow(raw, chanOffset);
        }

        // 级别共振：在"高周期窗口"上跑一次缠论（其次级别即当前级别），用同向信号窗口给本级别信号打标签。
        // 只用记账时间 ≤ 本级别信号时间的高周期信号 —— 无未来函数。
        Chan.ChanResult? htfChan = null;
        if (chanResult is not null && htfCandles is { Count: > 50 } htfForChan && htfInterval is not null
            && !ReferenceEquals(htfCandles, chanWindow))
        {
            var htfCloses = htfForChan.Select(c => c.Close).ToArray();
            var htfHist = Macd.Compute(htfCloses).Hist;
            var htfAtr = Atr.Compute(htfForChan.Select(c => c.High).ToArray(), htfForChan.Select(c => c.Low).ToArray(),
                htfCloses, 14);
            htfChan = Chan.ChanAnalyzer.Analyze(htfForChan, htfHist, htfAtr, chanOpt, candles, interval);
        }

        var confluenceWindow = chanOpt.ConfluenceWindowBars * MarketIntervals.IntervalSeconds(htfInterval ?? interval);
        var confluenceTags = chanResult is null
            ? new Dictionary<(long, string), string>()
            : Chan.ConfluenceTagger.Tag(htfChan?.Points ?? [], confluenceWindow,
                chanResult.Points.Select(p => (p.Time, p.Side)));

        // 联合打分：特征在"缠论内部窗口"上计算（与结构同源，保证不同 limit 下打分一致）
        decimal?[] scoreEma200 = [], scoreRsi = [], scoreHist = [];
        if (chanResult is not null)
        {
            var sc = chanWindow.Select(c => c.Close).ToArray();
            scoreEma200 = Ema.Compute(sc, 200);
            scoreRsi = Rsi.Compute(sc, 14);
            scoreHist = ReferenceEquals(chanWindow, candles) ? macd.Hist : Macd.Compute(sc).Hist;
        }

        var chanSignals = chanResult is null
            ? []
            : chanResult.Points.Where(p => p.Time >= candles[0].Time).Select(p =>
            {
                var bar = BarIndexOf(candles, p.Time);
                var regime = RegimeAt(bar);
                var confluence = confluenceTags.GetValueOrDefault((p.Time, p.Side));
                var scoreBar = BarIndexOf(chanWindow, p.Time);
                var jointScore = scoreBar >= 0 && scoreBar < scoreEma200.Length
                    ? JointScoreRules.Compute(
                        p.Price, scoreEma200[scoreBar],
                        scoreRsi.Length > scoreBar ? scoreRsi[scoreBar] : null,
                        scoreHist.Length > scoreBar ? scoreHist[scoreBar] : null,
                        p.Side, p.Kind, p.AreaRatio,
                        JointScoreRules.RiskPct(p.Price, p.StopPrice),
                        JointScoreRules.LagShare(p.Price, p.ReferencePrice, p.StopPrice))
                    : null;
                return new TradeSignal(
                    Time: p.Time,
                    Side: p.Side,
                    Source: "缠论",
                    Price: p.Price,
                    Note: $"[{p.Kind}] {p.Note}",
                    StopPrice: p.StopPrice,
                    IsConfirmed: true,
                    Adx: regime.Adx,
                    AtrPct: regime.AtrPct,
                    BandwidthPct: regime.BandwidthPct,
                    Confluence: confluence,
                    ReferencePrice: p.ReferencePrice,
                    Kind: p.Kind,
                    JointScore: jointScore);
            }).ToList();
        // 精简为纯缠论：信号只有缠论买卖点（EMA/RSI/MACD/结构 已降为图表指标，不再产生信号）

        // 多级别结构（高周期 / 本级别 / 次级别）：同一张图叠加显示，并标注本级别中枢的级别归属
        IReadOnlyList<ChanLevelStructure>? chanLevels = null;
        List<ChanPivotInfo>? annotatedPrimaryPivots = null;
        if (chanResult is not null)
        {
            var displayFrom = candles[0].Time;
            var displayTo = candles[^1].Time;
            var primaryPivots = Chan.ChanLevelMapper.ToPivotInfos(candles, chanResult, displayFrom, displayTo, 10);

            var higherPivots = htfChan is not null && htfCandles is not null
                ? Chan.ChanLevelMapper.ToPivotInfos(htfCandles, htfChan, displayFrom, displayTo, 6)
                : [];
            Chan.ChanResult? lowerChan = null;
            if (lowerLevelCandles is { Count: > 100 } && lowerLevelInterval is not null)
            {
                var ltfCloses = lowerLevelCandles.Select(c => c.Close).ToArray();
                lowerChan = Chan.ChanAnalyzer.Analyze(
                    lowerLevelCandles,
                    Macd.Compute(ltfCloses).Hist,
                    Atr.Compute(lowerLevelCandles.Select(c => c.High).ToArray(),
                        lowerLevelCandles.Select(c => c.Low).ToArray(), ltfCloses, 14),
                    chanOpt.Clone(requireSubLevelConfirm: false));
            }

            var lowerPivots = lowerChan is not null && lowerLevelCandles is not null
                ? Chan.ChanLevelMapper.ToPivotInfos(lowerLevelCandles, lowerChan, displayFrom, displayTo, 30)
                : [];

            annotatedPrimaryPivots = primaryPivots
                .Select(pv =>
                {
                    var (inside, lowerCount) = Chan.ChanLevelMapper.Annotate(pv, higherPivots, lowerPivots);
                    return pv with
                    {
                        InsideHigher = htfChan is null ? null : inside,
                        LowerPivotCount = lowerChan is null ? null : lowerCount,
                    };
                })
                .ToList();

            var chanLevelList = new List<ChanLevelStructure>();
            if (htfChan is not null && htfCandles is not null && htfInterval is not null)
            {
                chanLevelList.Add(new ChanLevelStructure("higher", htfInterval, higherPivots,
                    LastDirection(htfChan), htfChan.Points.Count, Chan.ChanLevelMapper.CoverageFromTime(htfCandles)));
            }

            chanLevelList.Add(new ChanLevelStructure("primary", interval, annotatedPrimaryPivots,
                LastDirection(chanResult), chanResult.Points.Count, Chan.ChanLevelMapper.CoverageFromTime(candles)));
            if (lowerChan is not null && lowerLevelCandles is not null && lowerLevelInterval is not null)
            {
                chanLevelList.Add(new ChanLevelStructure("lower", lowerLevelInterval, lowerPivots,
                    LastDirection(lowerChan), lowerChan.Points.Count,
                    Chan.ChanLevelMapper.CoverageFromTime(lowerLevelCandles)));
            }

            chanLevels = chanLevelList;
        }

        var series = new Dictionary<string, decimal?[]>(StringComparer.Ordinal)
        {
            ["ema20"] = ToNullable(ema20),
            ["ema50"] = ToNullable(ema50),
            ["ema200"] = ToNullable(ema200),
            ["rsi14"] = ToNullable(rsi),
            // atr14 不回传：前端零引用（ATR 已通过 regime.atrPct 提供）；500 根窗口下白占约 4KB/次
            // （2026-09-30 审查，见 PLAN §0.20。曾因并发编辑被还原，此处重新应用）
            ["bollUpper"] = ToNullable(boll.Upper),
            ["bollMiddle"] = ToNullable(boll.Middle),
            ["bollLower"] = ToNullable(boll.Lower),
        };
        if (chanResult is not null)
        {
            foreach (var (key, values) in chanResult.Series)
                series[key] = values;
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
            chanSignals,
            series,
            new MacdSeries(ToNullable(macd.Dif), ToNullable(macd.Dea), ToNullable(macd.Hist)),
            chanResult is null
                ? null
                : BuildChanSummary(candles, chanResult,
                    htfChan is null || htfInterval is null
                        ? null
                        : BuildHigherContext(htfCandles!, htfChan, htfInterval),
                    annotatedPrimaryPivots,
                    BuildStructurePosition(candles, chanResult, atr, chanLevels)),
            chanLevels);
    }

    /// <summary>高周期结构上下文：当前笔方向、价格相对最新中枢、最近买卖点。</summary>
    private static ChanContextSummary BuildHigherContext(
        IReadOnlyList<Candle> htfCandles, Chan.ChanResult htf, string htfInterval)
    {
        var lastStroke = htf.Strokes.Count > 0 ? htf.Strokes[^1] : default;
        var pivot = htf.Pivots.Count > 0 ? htf.Pivots[^1] : (Chan.ChanPivot?)null;
        var lastPoint = htf.Points.Count > 0 ? htf.Points[^1] : null;
        var last = htfCandles[^1].Close;
        return new ChanContextSummary(
            Interval: htfInterval,
            LastStrokeDirection: htf.Strokes.Count == 0 ? "none" : lastStroke.IsUp ? "up" : "down",
            PriceInPivot: pivot is { } p ? last >= p.Zd && last <= p.Zg : null,
            PivotZg: pivot?.Zg,
            PivotZd: pivot?.Zd,
            LastKind: lastPoint?.Kind,
            LastSide: lastPoint?.Side,
            LastTime: lastPoint?.Time,
            SignalCount: htf.Points.Count);
    }

    private static string LastDirection(Chan.ChanResult result) =>
        result.Strokes.Count == 0 ? "none" : result.Strokes[^1].IsUp ? "up" : "down";

    /// <summary>结构位置：本级别归属 + 各级别（含高/次级别）中枢归属对照（确定性计算，见 StructurePositionCalculator）。</summary>
    private static StructurePosition BuildStructurePosition(
        IReadOnlyList<Candle> candles, Chan.ChanResult result, decimal?[] atr,
        IReadOnlyList<ChanLevelStructure>? levels)
    {
        var last = candles[^1].Close;
        var levelPositions = (levels ?? [])
            .Select(lv => ToLevelPosition(lv, last))
            .ToList();
        var position = StructurePositionCalculator.Compute(candles, result, atr, levelPositions);
        // 关注度：由结构位置确定性推出（见 AttentionRules），界面置顶展示
        return position with { Attention = AttentionRules.Evaluate(position) };
    }

    /// <summary>某个级别的中枢归属（用该级别自己的中枢上下沿与同一现价判定）。</summary>
    private static LevelPosition ToLevelPosition(ChanLevelStructure level, decimal price)
    {
        var pivot = level.Pivots.Count > 0 ? level.Pivots[^1] : null;
        if (pivot is null)
            return new LevelPosition(level.Role, level.Interval, "none", null, null, null, 0);
        var zone = price >= pivot.Zd && price <= pivot.Zg ? "in" : price > pivot.Zg ? "above" : "below";
        var distance = zone switch
        {
            "above" => (price - pivot.Zg) / price,
            "below" => (pivot.Zd - price) / price,
            _ => (decimal?)null
        };
        return new LevelPosition(level.Role, level.Interval, zone, distance, pivot.Zg, pivot.Zd, pivot.Strokes);
    }

    private static ChanSummary BuildChanSummary(
        IReadOnlyList<Candle> candles, Chan.ChanResult result, ChanContextSummary? higherContext = null,
        IReadOnlyList<ChanPivotInfo>? annotatedPivots = null,
        StructurePosition? position = null)
    {
        var lastStroke = result.Strokes.Count > 0 ? result.Strokes[^1] : default;
        var pivot = result.Pivots.Count > 0 ? result.Pivots[^1] : (Chan.ChanPivot?)null;
        var lastPoint = result.Points.Count > 0 ? result.Points[^1] : null;
        var lastPrice = candles[^1].Close;

        // 笔数与中枢数都按"与显示窗口有交集"统计——与图表实际画出的内容一致，
        // 否则会出现"卡片写 11 个中枢、图上只有 6 个"的口径不符（2026-09-29 用户反馈）。
        var visiblePivots = annotatedPivots ?? BuildPivotInfos(candles, result);
        var visibleStrokes = result.Strokes.Count(s => s.EndBarIndex >= 0 && s.StartBarIndex < candles.Count);

        return new ChanSummary(
            LastStrokeDirection: result.Strokes.Count == 0 ? "none" : lastStroke.IsUp ? "up" : "down",
            LastStrokeConfirmed: result.Strokes.Count > 0 && lastStroke.IsConfirmed,
            StrokeCount: visibleStrokes,
            PivotCount: visiblePivots.Count,
            PivotZg: pivot?.Zg,
            PivotZd: pivot?.Zd,
            PivotStrokes: pivot?.StrokeCount ?? 0,
            PriceInPivot: pivot is { } p ? lastPrice >= p.Zd && lastPrice <= p.Zg : null,
            LastKind: lastPoint?.Kind,
            LastTime: lastPoint?.Time,
            LastPrice: lastPoint?.Price,
            LastNote: lastPoint?.Note,
            Pivots: visiblePivots,
            HigherContext: higherContext,
            Position: position);
    }

    /// <summary>
    /// 把在缠论内部窗口上算出的结果对齐到显示窗口。
    /// offset = 内部窗口根数 − 显示窗口根数：
    /// · offset &gt; 0：内部窗口更长 → 序列去掉头部 offset 项，索引整体左移；
    /// · offset &lt; 0：显示窗口更长（调用方请求超过内部固定窗口）→ 序列左补空，索引右移。
    /// </summary>
    private static Chan.ChanResult AlignToDisplayWindow(Chan.ChanResult raw, int offset)
    {
        var displayLength = raw.Series.Values.FirstOrDefault()?.Length - offset ?? 0;
        var series = new Dictionary<string, decimal?[]>(StringComparer.Ordinal);
        foreach (var (key, values) in raw.Series)
        {
            var aligned = new decimal?[displayLength];
            if (offset >= 0) Array.Copy(values, offset, aligned, 0, aligned.Length);
            else Array.Copy(values, 0, aligned, -offset, values.Length);
            series[key] = aligned;
        }

        Chan.ChanStroke Shift(Chan.ChanStroke s) => s with
        {
            StartBarIndex = s.StartBarIndex - offset,
            EndBarIndex = s.EndBarIndex - offset,
            StableFromBarIndex = s.StableFromBarIndex is { } b ? b - offset : null,
        };

        Chan.ChanFractal ShiftFractal(Chan.ChanFractal f) => f with
        {
            BarIndex = f.BarIndex - offset,
            ConfirmBarIndex = f.ConfirmBarIndex - offset,
        };

        Chan.ChanBuySellPoint ShiftPoint(Chan.ChanBuySellPoint p) => p with
        {
            ReferenceBarIndex = p.ReferenceBarIndex - offset,
        };

        return raw with
        {
            Series = series,
            Strokes = [.. raw.Strokes.Select(Shift)],
            Fractals = [.. raw.Fractals.Select(ShiftFractal)],
            Points = [.. raw.Points.Select(ShiftPoint)],
        };
    }

    /// <summary>
    /// 中枢 → 区间信息：委托 <see cref="Chan.ChanLevelMapper.ToPivotInfos"/>（唯一实现，
    /// 口径为"与显示窗口有交集即保留"，越界索引时间按等间隔外推——见该方法的修因说明）。
    /// </summary>
    private static IReadOnlyList<ChanPivotInfo> BuildPivotInfos(
        IReadOnlyList<Candle> candles, Chan.ChanResult result) =>
        Chan.ChanLevelMapper.ToPivotInfos(candles, result, candles[0].Time, candles[^1].Time, 8);

    /// <summary>按时间戳定位原始K线索引（找不到时回退到最后一根）。</summary>
    private static int BarIndexOf(IReadOnlyList<Candle> candles, long time)
    {
        for (var i = candles.Count - 1; i >= 0; i--)
            if (candles[i].Time == time)
                return i;
        return candles.Count - 1;
    }

    private static decimal?[] ToNullable(decimal?[] values) =>
        Array.ConvertAll(values, v => v is null ? null : v);
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
        decimal?[] ema50,
        decimal?[] ema200,
        AdxDmi.AdxDmiResult dmi)
    {
        var price = candles[^1].Close;
        decimal bull = 0, bear = 0;
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

    private static decimal? LastValid(decimal?[] values)
    {
        for (var i = values.Length - 1; i >= 0; i--)
            if (values[i] is not null)
                return values[i];
        return null;
    }
}

/// <summary>
/// 关键价格点位（概念二：支撑位与阻力位）。
/// 摆动高低点（±lookback 根K线的分形极值）按 0.5m×ATR 容差聚类，
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
        decimal?[] atr,
        int lookback = 3,
        int maxPerSide = 5)
    {
        var last = candles[^1];
        var tolerance = 0.5m * (LastValid(atr) ?? last.Close * 0.01m);

        var swings = FindSwings(candles, lookback).Select(s => s.Price).ToList();

        // 按时间序聚类：价差在容差内的摆动点视为同一位点，均值作价位，次数作强度
        var clustered = new List<(decimal Sum, int Count)>();
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

        return [.. resistances, .. supports];
    }

    private static decimal Pct(decimal level, decimal price) => Math.Round((level - price) / price * 100, 2);

    private static decimal? LastValid(decimal?[] values)
    {
        for (var i = values.Length - 1; i >= 0; i--)
            if (values[i] is not null)
                return values[i];
        return null;
    }
}
