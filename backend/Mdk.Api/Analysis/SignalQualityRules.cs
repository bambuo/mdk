using Mdk.Api.Domain;
using Mdk.Api.Indicators;

namespace Mdk.Api.Analysis;

/// <summary>
/// 信号准入分级规则（按**证据强度**，与周期/来源无关）：
/// "可参考"需同时满足 ① 独立波次 ≥30 ② 超额 t 值 ≥2 ③ 扣费后为正 ≥50%
/// ④ 中位超额 &gt;0（防少数大赢家拉高均值）⑤ 单一标的占比 ≤50%（防集中度）。
/// 逐项检查并给出第一个不满足的原因，便于前端展示与自查。
/// </summary>
public static class SignalQualityRules
{
    public static (string Grade, string Reason, decimal TopSymbolShare) Evaluate(IReadOnlyList<SignalJournalEntry> items)
    {
        var n = items.Count;
        var episodes = EpisodeRepresentatives(items);
        var topShare = n == 0 ? 0 : items.GroupBy(e => e.Symbol).Max(g => g.Count()) / (decimal)n;

        if (episodes.Count < 30)
            return ("样本不足", $"独立波次 {episodes.Count} < 30（原始 {n} 条）", topShare);

        var netPositive = items.Count(e => e.Outcome!.NetPositive == true) / (decimal)n;
        var episodeExcess = episodes.Select(e => e.Outcome!.Excess ?? 0).ToList();
        var medExcess = Median(items.Select(e => e.Outcome!.Excess ?? 0));

        var mean = episodeExcess.Average();
        var sd = episodeExcess.Count > 1
            ? DecimalMath.Sqrt(episodeExcess.Sum(v => (v - mean) * (v - mean)) / (episodeExcess.Count - 1))
            : 0;
        var t = sd > 0 ? mean / (sd / DecimalMath.Sqrt(episodeExcess.Count)) : 0;

        if (t < 2)
            return ("仅观察", $"超额 t={t:0.00m} < 2（未达统计显著）", topShare);
        if (netPositive < 0.5m)
            return ("仅观察", $"扣费后为正 {netPositive * 100:0}% < 50%", topShare);
        if (medExcess <= 0)
            return ("仅观察", $"中位超额 {medExcess * 100:+0.00m;-0.00m}% ≤ 0（均值被少数大赢家拉高）", topShare);
        if (topShare > 0.5m)
            return ("仅观察", $"单一标的占比 {topShare * 100:0}% > 50%（集中度过高）", topShare);
        return ("可参考", $"波次 {episodes.Count} · t={t:0.0m} · 扣费后为正 {netPositive * 100:0}% · 中位超额 {medExcess * 100:+0.00m;-0.00m}%", topShare);
    }

    /// <summary>独立波次数量（同币种/周期/方向、间隔 ≤24 根归为一波）。</summary>
    public static int EpisodeCount(IReadOnlyList<SignalJournalEntry> items) => EpisodeRepresentatives(items).Count;

    /// <summary>波次代表样本：同币种/周期/方向、间隔 ≤24 根归为一波，取每波首条。</summary>
    public static List<SignalJournalEntry> EpisodeRepresentatives(IReadOnlyList<SignalJournalEntry> items)
    {
        var reps = new List<SignalJournalEntry>();
        foreach (var group in items.GroupBy(e => (e.Symbol, e.Interval, e.Side)))
        {
            var barSeconds = MarketIntervals.IntervalSeconds(group.Key.Interval);
            long previous = long.MinValue;
            foreach (var entry in group.OrderBy(e => e.Time))
            {
                if (previous == long.MinValue || entry.Time - previous > barSeconds * 24)
                    reps.Add(entry);
                previous = entry.Time;
            }
        }
        return reps;
    }

    public static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
}
