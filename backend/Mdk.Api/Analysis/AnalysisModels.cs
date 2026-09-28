using Mdk.Api.Domain;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>趋势方向判定结果。Direction：LONG=做多，SHORT=做空，RANGE=观望。</summary>
public sealed record TrendResult(string Direction, int Score, IReadOnlyList<string> Reasons);

/// <summary>关键价格点位。Kind：support=支撑（现价下方），resistance=阻力（现价上方）。</summary>
public sealed record PriceLevel(string Kind, double Price, int Strength, double DistancePct);

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
    double Price,
    string Note,
    double? StopPrice,
    bool IsConfirmed,
    bool? TrendAligned,
    double? Adx = null,
    double? AtrPct = null,
    double? BandwidthPct = null);

/// <summary>信号发生时的市场状态快照。</summary>
public readonly record struct SignalRegime(double? Adx, double? AtrPct, double? BandwidthPct);

/// <summary>已确认的历史摆动点（用于结构信号，避免使用未来数据）。</summary>
public readonly record struct SwingPoint(int Index, double Price, bool IsHigh);

public sealed record MacdSeries(double?[] Dif, double?[] Dea, double?[] Hist);

/// <summary>一次完整的分析结果（REST /api/analysis 与 WS analysis 推送共用此结构）。</summary>
public sealed record AnalysisResult(
    string Market,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Interval,
    long LastTime,
    double LastPrice,
    TrendResult Trend,
    IReadOnlyList<PriceLevel> Levels,
    IReadOnlyList<TradeSignal> Signals,
    IReadOnlyDictionary<string, double?[]> Series,
    MacdSeries Macd);

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
    double LastPrice,
    double PriceChangePercent,
    double QuoteVolume);

public sealed record Ticker24h(string Symbol, double LastPrice, double PriceChangePercent, double QuoteVolume);
