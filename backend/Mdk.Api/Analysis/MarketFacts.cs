using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>合约当前资金费率与标记价（`/fapi/v1/premiumIndex`）。</summary>
public sealed record PremiumIndex(
    decimal MarkPrice,
    decimal IndexPrice,
    decimal? FundingRate,
    long? NextFundingTime);

/// <summary>持仓量历史点（`/futures/data/openInterestHist`）。</summary>
public sealed record OpenInterestPoint(long Time, decimal SumOpenInterest, decimal SumOpenInterestValue);

/// <summary>
/// 杠杆面事实（**只陈述事实，不含概率推断**）：资金费率与结算时点、持仓量及其 24h 变化、基差、主动买占比。
///
/// 现货没有资金费率与持仓量（相应字段为 null）；主动买占比两个市场都有（币安K线自带 takerBuyBaseVolume）。
/// 用途是回答"当前位置是否拥挤、突破是否由杠杆推动"——环境描述，**不产生信号、不进台账、不参与统计**。
/// </summary>
public sealed record LeverageFacts(
    decimal? FundingRate,
    long? NextFundingTime,
    decimal? OpenInterest,
    decimal? OpenInterestChangePct,
    decimal? MarkPrice,
    decimal? BasisPct,
    decimal? TakerBuyShare,
    /// <summary>主动买占比所用的K线根数。</summary>
    int TakerBars,
    /// <summary>近 24h 采集到的强平笔数（币安只推每秒每标的最大一笔，故**系统性少于真实清算量**）。</summary>
    int? LiquidationCount = null,
    /// <summary>近 24h 强平名义额（计价币计；同样受上述抽样限制）。</summary>
    decimal? LiquidationNotional = null,
    /// <summary>多头被强平占比（0~1）：&gt;0.5 表示这段时间以多头挨打为主。</summary>
    decimal? LongLiquidatedShare = null);

/// <summary>杠杆面事实的纯计算（可独立测试）。</summary>
public static class LeverageMath
{
    /// <summary>主动买占比：近 bars 根的主动买量 ÷ 总量（0~1；0.5=买卖均衡）。无成交量时返回 null。</summary>
    public static decimal? TakerBuyShare(IReadOnlyList<Candle> candles, int bars = 24)
    {
        if (candles.Count == 0) return null;
        var take = Math.Min(bars, candles.Count);
        var start = candles.Count - take;
        var total = 0m;
        var buy = 0m;
        for (var i = start; i < candles.Count; i++)
        {
            total += candles[i].Volume;
            buy += candles[i].TakerBuyVolume;
        }

        return total > 0 ? Math.Round(buy / total, 4) : null;
    }

    /// <summary>基差：标记价相对指数价的偏离（%）；指数价非正时返回 null。</summary>
    public static decimal? BasisPct(decimal markPrice, decimal indexPrice) =>
        indexPrice > 0 ? Math.Round((markPrice - indexPrice) / indexPrice * 100, 3) : null;

    /// <summary>变化百分比（%）；起点为 0 时返回 null。</summary>
    public static decimal? ChangePct(decimal first, decimal last) =>
        first != 0 ? Math.Round((last - first) / first * 100, 2) : null;

    /// <summary>年化资金费率（%）：单期费率 × 每日结算次数 × 365（合约默认每 8 小时一次）。</summary>
    public static decimal? AnnualizedFundingPct(decimal? fundingRate, int settlementsPerDay = 3) =>
        fundingRate is { } rate ? Math.Round(rate * settlementsPerDay * 365 * 100, 2) : null;
}
