using Mdk.Api.Domain;

namespace Mdk.Api.Models;

/// <summary>一根K线（已归一化，时间为秒级 Unix 时间戳；价格/量为 decimal）。</summary>
public readonly record struct Candle(
    long Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    /// <summary>主动买成交量（币安K线第 10 列 takerBuyBaseVolume）；不提供时为 0。</summary>
    decimal TakerBuyVolume = 0m);

/// <summary>币安 WS kline 推送归一化后的增量K线（当前K线实时更新，IsFinal 表示该K线已收盘）。</summary>
public sealed record KlineUpdate(
    MarketKind Market,
    TradingPair Pair,
    string Interval,
    long Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool IsFinal);
