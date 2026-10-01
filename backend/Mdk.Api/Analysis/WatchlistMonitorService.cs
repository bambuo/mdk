using System.Collections.Concurrent;
using Mdk.Api.Domain;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Analysis;

/// <summary>
/// 指标当前状态（监控快照的"指标状态"行）。指标已降为纯图表、不产生信号（用户拍板），
/// 因此这里展示的是**状态**（如 EMA 多头排列、RSI 47）而非买卖事件；
/// Tone 用于着色：bull=偏多 / bear=偏空 / neutral=中性。
/// </summary>
public sealed record WatchlistIndicatorState(string Source, string State, string Tone);

// 单个周期的监控快照（来自后台最新一次分析，只取监控页需要的摘要字段）。
public sealed record WatchlistIntervalSnapshot(
    string Interval,
    decimal LastPrice,
    long LastTime,
    string StrokeDirection,
    bool StrokeConfirmed,
    decimal? PivotZg,
    decimal? PivotZd,
    int PivotStrokes,
    bool? PriceInPivot,
    string? LastKind,
    string? LastSide,
    long? LastSignalTime,
    int? BarsSinceSignal,
    int SignalCount,
    // 各指标当前状态（EMA 排列 / RSI 位置 / MACD 动能；指标不产生信号，见用户拍板口径）。
    IReadOnlyList<WatchlistIndicatorState> IndicatorStates,
    long UpdatedAt);

// 监控条目视图：条目配置 + 各周期结构快照 + 跨周期共振判定。
public sealed record WatchlistItemView(
    string Market,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    IReadOnlyList<string> Intervals,
    bool Enabled,
    long CreatedAt,
    IReadOnlyList<WatchlistIntervalSnapshot> Snapshots,
    string Resonance);

/// <summary>
/// 后台监控：按用户**持久化**的监控列表逐 (市场, 交易对, 周期) 定时分析，无浏览器连接也持续运行，
/// 并把最新分析结果缓存在内存里供监控页读取。信号本身仍由 <see cref="AnalysisService"/> 写入台账，
/// 本服务只负责"按需监控 + 汇总呈现"。
/// </summary>
public sealed class WatchlistMonitorService(
    WatchlistStore store,
    AnalysisService analysisService,
    IOptions<SignalOptions> signalOptions,
    ILogger<WatchlistMonitorService> logger) : BackgroundService
{
    // 最新信号在多少根K线内仍算"新鲜"，用于跨周期共振判定。
    private const int ResonanceFreshBars = 30;

    private readonly SignalOptions _options = signalOptions.Value;
    private readonly ConcurrentDictionary<string, (AnalysisResult Result, long At)> _latest = new();

    public WatchlistStore Store => store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WatchlistEnabled)
        {
            logger.LogInformation("监控服务已禁用（Signal:WatchlistEnabled=false）");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.WatchlistIntervalSeconds));
        logger.LogInformation("监控服务已启动（每 {Seconds}s 分析监控列表）", interval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AnalyzeAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "监控列表分析循环失败");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // 立即分析整张监控列表一次。
    public async Task AnalyzeAllAsync(CancellationToken ct)
    {
        foreach (var item in store.All())
        {
            if (!item.Enabled) continue;
            await AnalyzeItemAsync(item, ct);
        }
    }

    // 立即分析单个条目（新增条目后预热，避免监控页空白）。
    public async Task AnalyzeItemAsync(WatchItem item, CancellationToken ct)
    {
        foreach (var itv in item.Intervals)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var result = await analysisService.AnalyzeAsync(item.Market, item.Pair, itv, 500, ct);
                _latest[Key(item.Market, item.Pair, itv)] = (result, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "监控分析失败 {Symbol} {Interval}", item.Pair.Display, itv);
            }
        }
    }

    // 监控页视图：条目 + 各周期快照 + 跨周期共振。
    public IReadOnlyList<WatchlistItemView> BuildView()
    {
        var views = new List<WatchlistItemView>();
        foreach (var item in store.All())
        {
            var snaps = new List<WatchlistIntervalSnapshot>();
            foreach (var itv in item.Intervals)
            {
                if (_latest.TryGetValue(Key(item.Market, item.Pair, itv), out var hit))
                    snaps.Add(ToSnapshot(itv, hit.Result, hit.At));
            }

            views.Add(new WatchlistItemView(
                item.Market.ToString().ToLowerInvariant(),
                item.Pair.Symbol, item.Pair.BaseAsset, item.Pair.QuoteAsset,
                item.Intervals, item.Enabled, item.CreatedAt, snaps, Resonance(snaps)));
        }

        return views;
    }

    private static WatchlistIntervalSnapshot ToSnapshot(string interval, AnalysisResult r, long at)
    {
        var chan = r.Chan;
        var barSeconds = MarketIntervals.IntervalSeconds(interval);
        int? barsSince = null;
        if (chan?.LastTime is { } t && r.LastTime >= t && barSeconds > 0)
        {
            barsSince = (int)((r.LastTime - t) / barSeconds);
        }

        return new WatchlistIntervalSnapshot(
            interval, r.LastPrice, r.LastTime,
            chan?.LastStrokeDirection ?? "none",
            chan?.LastStrokeConfirmed ?? false,
            chan?.PivotZg, chan?.PivotZd, chan?.PivotStrokes ?? 0, chan?.PriceInPivot,
            chan?.LastKind, SideOf(chan?.LastKind), chan?.LastTime, barsSince,
            r.Signals.Count, IndicatorStates(r), at);
    }

    // 指标当前状态：EMA 排列 / RSI 位置 / MACD 动能（取各序列最后一根，与图表同源）。
    private static IReadOnlyList<WatchlistIndicatorState> IndicatorStates(AnalysisResult r)
    {
        var states = new List<WatchlistIndicatorState>();
        var e20 = Last(r, "ema20");
        var e50 = Last(r, "ema50");
        var e200 = Last(r, "ema200");
        if (e20 is { } a && e50 is { } b && e200 is { } c)
        {
            var tone = a > b && b > c ? "bull" : a < b && b < c ? "bear" : "neutral";
            var emaLabel = tone switch { "bull" => "多头排列", "bear" => "空头排列", _ => "纠缠" };
            states.Add(new WatchlistIndicatorState("EMA", emaLabel, tone));
        }

        if (Last(r, "rsi14") is { } rsi)
        {
            var (label, tone) = rsi >= 70m ? ("超买", "bear") : rsi <= 30m ? ("超卖", "bull") : ("中性", "neutral");
            states.Add(new WatchlistIndicatorState("RSI", $"{label} {rsi:0}", tone));
        }

        var hist = r.Macd.Hist.LastOrDefault(v => v is not null);
        if (hist is { } h)
        {
            states.Add(new WatchlistIndicatorState("MACD", h >= 0m ? "多头动能" : "空头动能", h >= 0m ? "bull" : "bear"));
        }

        return states;
    }

    // 分析序列的最后一个非空值（序列与K线对齐，末端即"当前"）。
    private static decimal? Last(AnalysisResult r, string key) =>
        r.Series.TryGetValue(key, out var arr) ? arr.LastOrDefault(v => v is not null) : null;

    // 买卖点类别（"1买"…"3卖"）→ 方向。
    private static string? SideOf(string? kind) =>
        kind is null ? null : kind.EndsWith('买') ? "buy" : "sell";

    // 跨周期共振：只看"新鲜"（≤{ResonanceFreshBars} 根内）的最近买卖点；全同向=对齐，多空并存=分歧，其余=无。
    private static string Resonance(IReadOnlyList<WatchlistIntervalSnapshot> snaps)
    {
        var fresh = snaps
            .Where(s => s.LastSide is not null && s.BarsSinceSignal is <= ResonanceFreshBars)
            .ToList();
        if (fresh.Count == 0) return snaps.Count == 0 ? "pending" : "none";
        if (fresh.Count == 1) return "none";
        var buys = fresh.Count(s => s.LastSide == "buy");
        var sells = fresh.Count(s => s.LastSide == "sell");
        if (buys == 0 || sells == 0) return "aligned";
        return "mixed";
    }

    private static string Key(MarketKind market, TradingPair pair, string interval) =>
        $"{market}|{pair.Symbol}|{interval}";
}
