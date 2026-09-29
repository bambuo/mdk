using Mdk.Api.Domain;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>趋势方向判定结果。Direction：LONG=做多，SHORT=做空，RANGE=观望。</summary>
public sealed record TrendResult(string Direction, int Score, IReadOnlyList<string> Reasons);

/// <summary>关键价格点位。Kind：support=支撑（现价下方），resistance=阻力（现价上方）。</summary>
public sealed record PriceLevel(string Kind, decimal Price, int Strength, decimal DistancePct);

/// <summary>
/// 买卖点信号。Side：buy=买点（做多），sell=卖点（做空）。
/// IsConfirmed：true=基于已收盘K线确认（不可撤销）；false=盘中预警（基于未收盘K线，可能消失）。
/// TrendAligned：信号方向与高周期趋势（高一期 EMA50）是否同向；高周期数据不可用时为 null。
/// Adx/AtrPct/BandwidthPct：信号发生时的市场状态（用于事后按状态统计，判断信号的状态依赖性）。
/// </summary>
public sealed record TradeSignal(
    long Time,
    string Side,
    string Source,
    decimal Price,
    string Note,
    decimal? StopPrice,
    bool IsConfirmed,
    bool? TrendAligned,
    decimal? Adx = null,
    decimal? AtrPct = null,
    decimal? BandwidthPct = null,
    /// <summary>级别共振标签：aligned=窗口内有同向高周期缠论信号，counter=只有反向，none=无（仅缠论信号有值）。</summary>
    string? Confluence = null);

/// <summary>信号发生时的市场状态快照。</summary>
public readonly record struct SignalRegime(decimal? Adx, decimal? AtrPct, decimal? BandwidthPct);

/// <summary>已确认的历史摆动点（用于结构信号，避免使用未来数据）。</summary>
public readonly record struct SwingPoint(int Index, decimal Price, bool IsHigh);

public sealed record MacdSeries(decimal?[] Dif, decimal?[] Dea, decimal?[] Hist);

/// <summary>一个中枢的区间信息（供前端绘制中枢色带与列表）。</summary>
public sealed record ChanPivotInfo(
    long FromTime,
    long ToTime,
    decimal Zg,
    decimal Zd,
    int Strokes,
    bool IsConfirmed,
    /// <summary>是否落在某个高周期中枢内（多级别归属，仅本级别中枢有值）。</summary>
    bool? InsideHigher = null,
    /// <summary>内部包含的次级别中枢数量（仅本级别中枢有值）。</summary>
    int? LowerPivotCount = null);

/// <summary>某个周期的结构摘要（多级别叠加视图用）。</summary>
public sealed record ChanLevelStructure(
    /// <summary>higher=高周期 / primary=本级别 / lower=次级别</summary>
    string Role,
    string Interval,
    IReadOnlyList<ChanPivotInfo> Pivots,
    string LastStrokeDirection,
    int SignalCount,
    /// <summary>该级别数据的起始时间（数据不足以覆盖整个显示窗口时，前端据此提示）。</summary>
    long CoverageFromTime);

/// <summary>高周期（L1）结构上下文：用于"级别共振"展示（当前笔方向、价格相对最新中枢、最近买卖点）。</summary>
public sealed record ChanContextSummary(
    string Interval,
    string LastStrokeDirection,
    bool? PriceInPivot,
    decimal? PivotZg,
    decimal? PivotZd,
    string? LastKind,
    string? LastSide,
    long? LastTime,
    int SignalCount);

/// <summary>缠论结构摘要（供前端"缠论结构"卡片与信号标注使用）。</summary>
public sealed record ChanSummary(
    string LastStrokeDirection,
    bool LastStrokeConfirmed,
    int StrokeCount,
    int PivotCount,
    decimal? PivotZg,
    decimal? PivotZd,
    int PivotStrokes,
    bool? PriceInPivot,
    string? LastKind,
    long? LastTime,
    decimal? LastPrice,
    string? LastNote,
    IReadOnlyList<ChanPivotInfo> Pivots,
    /// <summary>高周期结构上下文（无高周期数据时为 null）。</summary>
    ChanContextSummary? HigherContext = null,
    /// <summary>结构模式：stroke=笔中枢（默认）/ segment=线段中枢。</summary>
    string LevelMode = "stroke",
    /// <summary>线段数量（stroke 模式下为 0）。</summary>
    int SegmentCount = 0);

/// <summary>一次完整的分析结果（REST /api/analysis 与 WS analysis 推送共用此结构）。</summary>
public sealed record AnalysisResult(
    string Market,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Interval,
    long LastTime,
    decimal LastPrice,
    TrendResult Trend,
    IReadOnlyList<PriceLevel> Levels,
    IReadOnlyList<TradeSignal> Signals,
    IReadOnlyDictionary<string, decimal?[]> Series,
    MacdSeries Macd,
    ChanSummary? Chan = null,
    /// <summary>多级别结构（高周期 / 本级别 / 次级别）；无对应数据时该级别缺省。</summary>
    IReadOnlyList<ChanLevelStructure>? ChanLevels = null);

public sealed record KlinesResponse(
    string Market,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Interval,
    IReadOnlyList<Candle> Candles);

public sealed record SymbolQuote(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal LastPrice,
    decimal PriceChangePercent,
    decimal QuoteVolume);

public sealed record Ticker24h(string Symbol, decimal LastPrice, decimal PriceChangePercent, decimal QuoteVolume);
