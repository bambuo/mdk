using Mdk.Api.Domain;
using Mdk.Api.Indicators;

namespace Mdk.Api.Analysis;

/// <summary>
/// 某语境（来源 × 类别 × 周期）下的信号可信度——由**台账里的实际结果**算出，
/// 而不是启发式分数。样本不足时 <see cref="Sufficient"/> 为 false，界面必须显示"样本不足"而非给出胜率。
/// </summary>
public sealed record CredibilityBucket(
    /// <summary>买卖点类别（如 "3买"）。精简为纯缠论后不再需要来源维度。</summary>
    string? Kind,
    string Interval,
    /// <summary>原始样本数。</summary>
    int N,
    /// <summary>独立波次（同标的/周期/方向，间隔 ≤24 根归为一波）——统计显著性的有效样本量。</summary>
    int NEpisodes,
    /// <summary>波次数是否达到可给出胜率的最低要求。</summary>
    bool Sufficient,
    /// <summary>经验胜率（收益 > 0 的比例，波次口径）。</summary>
    decimal WinRate,
    /// <summary>胜率的 95% Wilson 区间下界。</summary>
    decimal WinRateLow,
    /// <summary>胜率的 95% Wilson 区间上界。</summary>
    decimal WinRateHigh,
    /// <summary>扣费后为正的比例（波次口径）。</summary>
    decimal NetPositiveRate,
    /// <summary>中位超额（剔除同期市场漂移后），比均值抗极值。</summary>
    decimal MedianExcess,
    /// <summary>典型风险单位（入场到结构失效位的距离占入场价比例）中位数——用于判断止损是否过远。</summary>
    decimal MedianRiskPct);

/// <summary>
/// 可信度口径（纯函数，可独立测试）。行业做法：**不给没有样本支撑的"置信度"**，
/// 给出经验频率 + 置信区间，样本不足就明说；中位数与扣费后口径优先于均值。
/// </summary>
public static class SignalCredibilityRules
{
    /// <summary>可给出胜率徽章的最低独立波次数（与分级规则一致：少于此时只显示"样本不足"）。</summary>
    public const int MinEpisodes = 30;

    /// <summary>95% 正态分位（Wilson 区间用）。</summary>
    private const decimal Z = 1.96m;

    public static CredibilityBucket Build(
        string? kind, string interval, IReadOnlyList<SignalEntry> items)
    {
        var episodes = SignalQualityRules.EpisodeRepresentatives(items);
        var n = episodes.Count;
        var wins = episodes.Count(e => e.Outcome?.Ret > 0);
        var netPositive = episodes.Count(e => e.Outcome?.NetPositive == true);
        var winRate = n == 0 ? 0m : wins / (decimal)n;
        var (low, high) = Wilson(wins, n);
        var excess = episodes
            .Where(e => e.Outcome?.Excess is not null)
            .Select(e => e.Outcome!.Excess!.Value)
            .ToList();
        var risks = episodes
            .Where(e => e.Price != 0 && e.StopPrice is not null)
            .Select(e => Math.Abs(e.Price - e.StopPrice!.Value) / e.Price)
            .ToList();

        return new CredibilityBucket(
            Kind: kind,
            Interval: interval,
            N: items.Count,
            NEpisodes: n,
            Sufficient: n >= MinEpisodes,
            WinRate: winRate,
            WinRateLow: low,
            WinRateHigh: high,
            NetPositiveRate: n == 0 ? 0m : netPositive / (decimal)n,
            MedianExcess: Median(excess),
            MedianRiskPct: Median(risks));
    }

    /// <summary>
    /// 从台账快照构建某语境（市场 × 类别 × 周期，缠论）的可信度：
    /// 只统计已评估样本，排除锚定币；同一信号先以「盘中预警」再以「收盘确认」各记一条时，
    /// 按信号本体去重并优先确认版本。与 AnalysisService 的可信度表同一口径，供监控页等复用。
    /// </summary>
    public static CredibilityBucket BuildFromStore(
        string? kind, string interval, MarketKind market, IReadOnlyList<SignalEntry> snapshot)
    {
        var items = snapshot
            .Where(e => e.Market == market
                        && e.Interval == interval
                        && e.Source == "缠论"
                        && e.Kind == kind
                        && !e.Pair.IsPegged
                        && e.Outcome is { Status: "ok" })
            .GroupBy(e => (e.Pair, e.Interval, e.Source, e.Kind, e.Side, e.Time))
            .Select(g => g.FirstOrDefault(e => e.IsConfirmed) ?? g.First())
            .ToList();
        return Build(kind, interval, items);
    }

    /// <summary>
    /// 胜率的 95% Wilson 区间：小样本下比"胜率 ± 1.96×标准误"稳健（后者在 0/1 附近会跑出 [0,1] 之外）。
    /// n=0 时返回 [0,1]。
    /// </summary>
    public static (decimal Low, decimal High) Wilson(int wins, int n)
    {
        if (n <= 0) return (0m, 1m);
        var p = wins / (decimal)n;
        var denominator = 1 + Z * Z / n;
        var center = (p + Z * Z / (2 * n)) / denominator;
        var half = Z * DecimalMath.Sqrt(p * (1 - p) / n + Z * Z / (4m * n * n)) / denominator;
        return (decimal.Max(0m, center - half), decimal.Min(1m, center + half));
    }

    private static decimal Median(List<decimal> values)
    {
        if (values.Count == 0) return 0m;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
