using Mdk.Api.Domain;
using Mdk.Api.Indicators;

namespace Mdk.Api.Analysis;

/// <summary>
/// 信号分级规则（按**证据强度**描述样本，与周期/来源无关），由低到高：
/// "样本不足" → "仅观察" → "可参考"。
/// "可参考"需同时满足 ① 独立波次 ≥30 ② 超额 t 值 ≥2 ③ 扣费后为正 ≥50%
/// ④ 中位超额 &gt;0（防少数大赢家拉高均值）⑤ 单一标的占比 ≤50%（防集中度）。
///
/// **"可实盘"档已废弃（2026-09-30 用户拍板）**：其判据依赖"统计显著优势"这一本质上不确定的量——
/// 本项目先后在 13 个门控、打分 v1/v2、以及 181 天未挖窗口（4762 条样本、1383 波）上检验，
/// 全部未通过（PLAN §0.19/§0.20/§0.21）。既然优势无法被证据确立，就不该由系统给出交易授权；
/// 分级只陈述"样本证据强度"，不构成任何可交易/可实盘的含义。
/// 逐项检查并给出第一个不满足的原因，便于前端展示与自查。
/// </summary>
public static class SignalQualityRules
{
    public static (string Grade, string Reason, decimal TopSymbolShare) Evaluate(IReadOnlyList<SignalEntry> items)
    {
        var n = items.Count;
        var episodes = EpisodeRepresentatives(items);
        // 全部口径统一到**独立波次**：同一指标在不同位置（分级 / 可信度徽章 / 实盘判据）必须同底，
        // 否则"胜率 48%（原始）"与"胜率 51%（波次）"会同时出现在界面上而无法比较。
        var m = episodes.Count;
        var topShare = m == 0 ? 0 : episodes.GroupBy(e => e.Pair).Max(g => g.Count()) / (decimal)m;

        if (m < 30)
            return ("样本不足", $"独立波次 {m} < 30（原始 {n} 条）", topShare);

        var netPositive = episodes.Count(e => e.Outcome!.NetPositive == true) / (decimal)m;
        var episodeExcess = episodes.Select(e => e.Outcome!.Excess ?? 0).ToList();
        var medExcess = Median(episodeExcess);

        var mean = episodeExcess.Average();
        var sd = episodeExcess.Count > 1
            ? DecimalMath.Sqrt(episodeExcess.Sum(v => (v - mean) * (v - mean)) / (episodeExcess.Count - 1))
            : 0;
        var t = sd > 0 ? mean / (sd / DecimalMath.Sqrt(episodeExcess.Count)) : 0;

        if (t < 2)
            return ("仅观察", $"超额 t={t:0.00} < 2（未达统计显著）", topShare);
        if (netPositive < 0.5m)
            return ("仅观察", $"扣费后为正 {netPositive * 100:0}% < 50%", topShare);
        if (medExcess <= 0)
            return ("仅观察", $"中位超额 {medExcess * 100:+0.00m;-0.00m}% ≤ 0（均值被少数大赢家拉高）", topShare);
        if (topShare > 0.5m)
            return ("仅观察", $"单一标的占比 {topShare * 100:0}% > 50%（集中度过高）", topShare);
        return ("可参考", $"波次 {m} · t={t:0.0} · 扣费后为正 {netPositive * 100:0}% · 中位超额 {medExcess * 100:+0.00;-0.00}%", topShare);
    }

    /// <summary>记账K线收盘后多久内落库算"实时"（≤1.5 根K线；事后回算的历史样本不算）。
    /// 仅用于统计"窗口内有多少实时样本"这一**事实**——"可实盘"判据已废弃，不再有门槛含义。</summary>
    public static bool IsRealtimeRecorded(SignalEntry e)
    {
        var seconds = MarketIntervals.IntervalSeconds(e.Interval);
        return seconds > 0 && e.RecordedAt - e.Time <= (long)(1.5 * seconds);
    }

    /// <summary>独立波次数量（同币种/周期/方向、间隔 ≤24 根归为一波）。</summary>
    public static int EpisodeCount(IReadOnlyList<SignalEntry> items) => EpisodeRepresentatives(items).Count;

    /// <summary>波次代表样本：同币种/周期/方向、间隔 ≤24 根归为一波，取每波首条。</summary>
    public static List<SignalEntry> EpisodeRepresentatives(IReadOnlyList<SignalEntry> items)
    {
        var reps = new List<SignalEntry>();
        foreach (var group in items.GroupBy(e => (e.Pair, e.Interval, e.Side)))
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
