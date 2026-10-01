using Mdk.Api.Indicators;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>
/// 位点"守住/跌破"检验的**预注册**参数与判据（2026-10-01 定稿，跑之前写死，跑之后不得调整）。
///
/// 检验的命题只有一句：**位点的位置本身是否比"同样距离的任意价位"更容易挡住价格**。
/// 因此必须有对照组：同一次评估、同一距离、同侧，但价格不是位点。若两者无差异，
/// 位点就只是把当前价按 ATR 切了几段，不承载额外信息。
/// </summary>
public sealed record LevelHoldStudyOptions
{
    /// <summary>位点窗口：与线上口径一致（1000 根）。</summary>
    public int AnalysisBars { get; init; } = 1000;

    /// <summary>评估步长（根）：每隔多少根取一个评估时点，避免相邻时点高度重叠。</summary>
    public int StepBars { get; init; } = 4;

    /// <summary>触碰窗口（根）：评估时点之后多久内寻找触碰。</summary>
    public int TouchWindow { get; init; } = 200;

    /// <summary>持有窗口（根）：触碰之后多久内判定结局；超时未决记为 pending（不计入比例）。</summary>
    public int HoldWindow { get; init; } = 60;

    /// <summary>只检验距现价 ≤ 该 ATR 倍数的位点（更远的位点与当前决策无关）。</summary>
    public decimal WatchAtr { get; init; } = 3m;

    /// <summary>触碰判定容差（ATR 倍数）：影线进入位点附近即算触碰。</summary>
    public decimal TouchTolAtr { get; init; } = 0.25m;

    /// <summary>守住判定：向"位点应起作用"的方向走出该 ATR 倍数。</summary>
    public decimal BounceAtr { get; init; } = 1m;

    /// <summary>跌破判定：收盘价穿过位点该 ATR 倍数；同一根内两者都满足时按跌破计（取更硬的事实）。</summary>
    public decimal BreakAtr { get; init; } = 0.5m;

    /// <summary>判据①：真实位点守住率 − 对照组守住率 ≥ 该值（10 个百分点）。</summary>
    public decimal MinEdge { get; init; } = 0.10m;

    /// <summary>判据②：独立波次下限。</summary>
    public int MinEpisodes { get; init; } = 100;

    /// <summary>判据③：单一标的占全部波次的比例上限。</summary>
    public decimal MaxTopSymbolShare { get; init; } = 0.50m;

    /// <summary>同标的同周期同方向在该根数内聚为一波（与台账口径一致：24 根）。</summary>
    public int EpisodeGapBars { get; init; } = 24;

    public PriceLevelOptions Levels { get; init; } = new();
}

/// <summary>一次位点事件（或一次对照事件）。Outcome ∈ held/broke/pending/noTouch。</summary>
public sealed record LevelHoldEvent(
    string Symbol,
    string Interval,
    string Side,
    bool IsControl,
    string Source,
    decimal Score,
    long Time,
    decimal LevelPrice,
    decimal Close,
    string Outcome,
    int BarsToOutcome);

/// <summary>分组结果：比例只按**独立波次**计算（原始事件数另记，避免时间聚集虚高显著性）。</summary>
public sealed record LevelHoldGroup(
    string Label,
    int Events,
    int Episodes,
    int Held,
    int Broke,
    decimal Rate,
    decimal Low,
    decimal High)
{
    public int Pending => Events - Episodes - NoTouchCount;
    public int NoTouchCount => FieldNoTouch;
    internal int FieldNoTouch { get; init; }
}

public sealed record LevelHoldStudyResult(
    IReadOnlyList<LevelHoldGroup> Groups,
    LevelHoldGroup Control,
    decimal TopSymbolShare,
    bool Passed,
    IReadOnlyList<string> Reasons);

/// <summary>位点"守住/跌破"检验（纯函数；事件采集与汇总分开，便于单测）。</summary>
public static class LevelHoldStudy
{
    /// <summary>采集事件：在每个评估时点算一次位点，按规则找触碰与结局；同时生成同距离的对照事件。</summary>
    public static IReadOnlyList<LevelHoldEvent> Collect(
        string symbol,
        string interval,
        IReadOnlyList<Candle> candles,
        LevelHoldStudyOptions options)
    {
        var events = new List<LevelHoldEvent>();
        if (candles.Count < options.AnalysisBars + options.TouchWindow) return events;

        var atr = Atr.Compute(
            [.. candles.Select(c => c.High)],
            [.. candles.Select(c => c.Low)],
            [.. candles.Select(c => c.Close)], 14);

        for (var t = options.AnalysisBars - 1; t < candles.Count - 1; t += options.StepBars)
        {
            if (atr[t] is not { } unit || unit <= 0) continue;
            var window = candles.Skip(t - options.AnalysisBars + 1).Take(options.AnalysisBars).ToList();
            var levels = PriceLevels.Analyze(window, options.Levels);
            var close = candles[t].Close;

            foreach (var level in levels)
            {
                var distance = Math.Abs(level.Price - close);
                if (distance > options.WatchAtr * unit || distance < 0.1m * unit) continue;
                var side = level.Price < close ? "support" : "resistance";

                var real = Evaluate(candles, atr, t, level.Price, side, options);
                events.Add(new LevelHoldEvent(symbol, interval, side, false, SourceOf(level), level.Score,
                    candles[t].Time, level.Price, close, real.Outcome, real.Bars));

                // 对照：同一距离、同一侧，但价格**不是**位点；若恰好落在另一位点附近则跳过（避免对照被污染）
                var placebo = side == "support" ? close - distance : close + distance;
                if (placebo <= 0 || levels.Any(l => Math.Abs(l.Price - placebo) <= options.TouchTolAtr * unit)) continue;
                var control = Evaluate(candles, atr, t, placebo, side, options);
                events.Add(new LevelHoldEvent(symbol, interval, side, true, "对照", 0m,
                    candles[t].Time, placebo, close, control.Outcome, control.Bars));
            }
        }

        return events;
    }

    /// <summary>汇总：按独立波次算守住率与 Wilson 区间，并对照预注册判据。</summary>
    public static LevelHoldStudyResult Summarize(
        IReadOnlyList<LevelHoldEvent> events,
        LevelHoldStudyOptions options,
        IReadOnlyDictionary<string, long> barSeconds)
    {
        var real = events.Where(e => !e.IsControl).ToList();
        var control = events.Where(e => e.IsControl).ToList();

        var controlGroup = Group("对照（同距离非位点）", control, options, barSeconds);
        var groups = new List<LevelHoldGroup>
        {
            Group("全部位点", real, options, barSeconds),
            Group("支撑", [.. real.Where(e => e.Side == "support")], options, barSeconds),
            Group("阻力", [.. real.Where(e => e.Side == "resistance")], options, barSeconds),
            Group("仅摆动点", [.. real.Where(e => e.Source == "摆动点")], options, barSeconds),
            Group("含客观锚点", [.. real.Where(e => e.Source != "摆动点")], options, barSeconds),
            Group("强度分 ≥ 2", [.. real.Where(e => e.Score >= 2m)], options, barSeconds),
            Group("强度分 < 2", [.. real.Where(e => e.Score < 2m)], options, barSeconds),
        };

        var all = groups[0];
        var topSymbolShare = TopSymbolShare(real, options, barSeconds);
        var reasons = new List<string>();
        var edge = all.Rate - controlGroup.Rate;
        if (edge < options.MinEdge)
            reasons.Add($"真实位点守住率 {all.Rate:P1} 相对对照 {controlGroup.Rate:P1} 的优势 {edge:P1} < 判据 {options.MinEdge:P0}");
        if (all.Low <= controlGroup.Rate)
            reasons.Add($"真实位点守住率的 95% 区间下界 {all.Low:P1} 未超过对照点估计 {controlGroup.Rate:P1}");
        if (all.Episodes < options.MinEpisodes)
            reasons.Add($"独立波次 {all.Episodes} < 判据 {options.MinEpisodes}");
        if (topSymbolShare > options.MaxTopSymbolShare)
            reasons.Add($"单一标的占比 {topSymbolShare:P0} > 判据 {options.MaxTopSymbolShare:P0}");

        return new LevelHoldStudyResult(groups, controlGroup, topSymbolShare, reasons.Count == 0, reasons);
    }

    private static (string Outcome, int Bars) Evaluate(
        IReadOnlyList<Candle> candles, decimal?[] atr, int t, decimal price, string side, LevelHoldStudyOptions options)
    {
        var isSupport = side == "support";
        var touchAt = -1;
        var touchEnd = Math.Min(candles.Count - 1, t + options.TouchWindow);
        for (var i = t + 1; i <= touchEnd; i++)
        {
            if (atr[i] is not { } unit || unit <= 0) continue;
            var touched = isSupport
                ? candles[i].Low <= price + options.TouchTolAtr * unit
                : candles[i].High >= price - options.TouchTolAtr * unit;
            if (touched)
            {
                touchAt = i;
                break;
            }
        }

        if (touchAt < 0) return ("noTouch", 0);

        var holdEnd = Math.Min(candles.Count - 1, touchAt + options.HoldWindow);
        for (var j = touchAt; j <= holdEnd; j++)
        {
            if (atr[j] is not { } unit || unit <= 0) continue;
            var broke = isSupport
                ? candles[j].Close < price - options.BreakAtr * unit
                : candles[j].Close > price + options.BreakAtr * unit;
            if (broke) return ("broke", j - touchAt);

            var held = isSupport
                ? candles[j].High >= price + options.BounceAtr * unit
                : candles[j].Low <= price - options.BounceAtr * unit;
            if (held) return ("held", j - touchAt);
        }

        return ("pending", 0);
    }

    /// <summary>按独立波次汇总：同标的同周期同方向、间隔 ≤ EpisodeGapBars 根的事件算同一波，取首个代表。</summary>
    private static LevelHoldGroup Group(
        string label, IReadOnlyList<LevelHoldEvent> events, LevelHoldStudyOptions options,
        IReadOnlyDictionary<string, long> barSeconds)
    {
        var representatives = new List<LevelHoldEvent>();
        foreach (var series in events.GroupBy(e => (e.Symbol, e.Interval, e.Side)))
        {
            var gap = barSeconds.TryGetValue(series.Key.Interval, out var seconds)
                ? seconds * options.EpisodeGapBars
                : 0;
            long lastTime = long.MinValue;
            foreach (var e in series.OrderBy(e => e.Time))
            {
                if (gap > 0 && e.Time - lastTime <= gap) continue;
                representatives.Add(e);
                lastTime = e.Time;
            }
        }

        var held = representatives.Count(e => e.Outcome == "held");
        var broke = representatives.Count(e => e.Outcome == "broke");
        var decided = held + broke;
        var (low, high) = decided > 0 ? EvidenceRules.Wilson(held, decided) : (0m, 0m);
        return new LevelHoldGroup(label, events.Count, representatives.Count, held, broke,
            decided > 0 ? Math.Round((decimal)held / decided, 4) : 0m, low, high)
        {
            FieldNoTouch = representatives.Count(e => e.Outcome == "noTouch"),
        };
    }

    private static decimal TopSymbolShare(
        IReadOnlyList<LevelHoldEvent> events, LevelHoldStudyOptions options, IReadOnlyDictionary<string, long> barSeconds)
    {
        if (events.Count == 0) return 0m;
        var bySymbol = events.GroupBy(e => (e.Symbol, e.Interval, e.Side));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var series in bySymbol)
        {
            var gap = barSeconds.TryGetValue(series.Key.Interval, out var seconds)
                ? seconds * options.EpisodeGapBars
                : 0;
            long lastTime = long.MinValue;
            foreach (var e in series.OrderBy(e => e.Time))
            {
                if (gap > 0 && e.Time - lastTime <= gap) continue;
                lastTime = e.Time;
                counts[e.Symbol] = counts.GetValueOrDefault(e.Symbol) + 1;
                total++;
            }
        }

        return total == 0 ? 0m : Math.Round((decimal)counts.Values.Max() / total, 4);
    }

    private static string SourceOf(PriceLevel level) =>
        level.Sources.Any(s => s != "摆动点") ? string.Join('/', level.Sources.Where(s => s != "摆动点")) : "摆动点";
}
