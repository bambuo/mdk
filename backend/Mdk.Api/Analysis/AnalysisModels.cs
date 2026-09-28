using Mdk.Api.Domain;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>趋势方向判定结果。Direction：LONG=做多，SHORT=做空，RANGE=观望。</summary>
public sealed record TrendResult(string Direction, int Score, IReadOnlyList<string> Reasons);

/// <summary>关键价格点位。Kind：support=支撑（现价下方），resistance=阻力（现价上方）。</summary>
public sealed record PriceLevel(string Kind, double Price, int Strength, double DistancePct);

/// <summary>买卖点信号。Side：buy=买点（做多），sell=卖点（做空）。</summary>
public sealed record TradeSignal(
    long Time,
    string Side,
    string Source,
    double Price,
    string Note,
    double? StopPrice);

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
