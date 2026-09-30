using Mdk.Api.Domain;
using Mdk.Api.Indicators;

namespace Mdk.Api.Analysis;

/// <summary>
/// 信号准入分级规则（按**证据强度**，与周期/来源无关），由低到高：
/// "样本不足" → "仅观察" → "可参考" → "可实盘"。
/// "可参考"需同时满足 ① 独立波次 ≥30 ② 超额 t 值 ≥2 ③ 扣费后为正 ≥50%
/// ④ 中位超额 &gt;0（防少数大赢家拉高均值）⑤ 单一标的占比 ≤50%（防集中度）。
/// "可实盘"在"可参考"之上再过**实盘晋升判据**（见 <see cref="TradableGate"/>，预注册 2026-09-30，用户拍板写死）：
/// 只认**实时落库**的样本（回算历史不算数），全部达标才亮"可实盘"。
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
        var refReason = $"波次 {m} · t={t:0.0} · 扣费后为正 {netPositive * 100:0}% · 中位超额 {medExcess * 100:+0.00;-0.00}%";
        var realtime = episodes.Where(IsRealtimeRecorded).ToList();
        var gate = TradableGate(realtime);
        return gate.Ok
            ? ("可实盘", $"实时{gate.Reason}", topShare)
            : ("可参考", $"{refReason}；实盘判据未达标：{gate.Reason}", topShare);
    }

    /// <summary>晋升"可实盘"的最低实时独立波次。</summary>
    public const int RealtimeMinEpisodes = 100;

    /// <summary>记账K线收盘后多久内落库算"实时"（≤1.5 根K线；事后回算的历史样本不算数）。</summary>
    private const double RealtimeLagFactor = 1.5;

    /// <summary>是否实时落库的记录（用于实盘晋升判据的样本口径）。</summary>
    public static bool IsRealtimeRecorded(SignalEntry e)
    {
        var seconds = MarketIntervals.IntervalSeconds(e.Interval);
        return seconds > 0 && e.RecordedAt - e.Time <= (long)(RealtimeLagFactor * seconds);
    }

    /// <summary>
    /// 实盘晋升判据（预注册 2026-09-30，**判据先于数据写定**）——在"可参考"之上，只看实时落库的独立波次：
    /// ① 实时独立波次 ≥100 ② 超额 t ≥2 ③ 中位超额 &gt;0 ④ 扣费后为正 ≥55%
    /// ⑤ 时间前后分半的均值都 &gt;0（样本外不失效）⑥ 单一标的占比 ≤40%
    /// ⑦ 持有期最大浮亏（MAE）中位 ≤ 1R（止损设计能兜住典型回撤）。
    /// 返回 (是否全部达标, 第一个不达标项或达标摘要)。
    /// </summary>
    public static (bool Ok, string Reason) TradableGate(IReadOnlyList<SignalEntry> realtimeEpisodes)
    {
        var reps = realtimeEpisodes.ToList();
        if (reps.Count < RealtimeMinEpisodes)
            return (false, $"实时独立波次 {reps.Count} < {RealtimeMinEpisodes}");

        var excess = reps.Select(e => e.Outcome!.Excess ?? 0).ToList();
        var mean = excess.Average();
        var sd = excess.Count > 1
            ? DecimalMath.Sqrt(excess.Sum(v => (v - mean) * (v - mean)) / (excess.Count - 1))
            : 0;
        var t = sd > 0 ? mean / (sd / DecimalMath.Sqrt(excess.Count)) : 0;
        if (t < 2m)
            return (false, $"实时超额 t={t:0.00} < 2");

        var medExcess = Median(excess);
        if (medExcess <= 0)
            return (false, $"实时中位超额 {medExcess * 100:+0.00;-0.00}% ≤ 0");

        var netPositive = reps.Count(e => e.Outcome!.NetPositive == true) / (decimal)reps.Count;
        if (netPositive < 0.55m)
            return (false, $"扣费后为正 {netPositive * 100:0}% < 55%");

        var ordered = reps.OrderBy(e => e.Time).ToList();
        var half = ordered.Count / 2;
        var firstMean = ordered.Take(half).Select(e => e.Outcome!.Excess ?? 0).Average();
        var secondMean = ordered.Skip(half).Select(e => e.Outcome!.Excess ?? 0).Average();
        if (firstMean <= 0 || secondMean <= 0)
            return (false, $"时间分半不稳定（前半 {firstMean * 100:+0.00;-0.00}% / 后半 {secondMean * 100:+0.00;-0.00}%，需都为正）");

        var topShare = reps.GroupBy(e => e.Pair).Max(g => g.Count()) / (decimal)reps.Count;
        if (topShare > 0.40m)
            return (false, $"单一标的占比 {topShare * 100:0}% > 40%（集中度过高）");

        // 模式变量跨 lambda 不可见，用普通循环表达"MAE ÷ 风险单位"
        var maeInR = new List<decimal>();
        foreach (var e in reps)
        {
            if (e.Outcome!.Mae is not { } mae || e.Price == 0 || e.StopPrice is not { } stop) continue;
            maeInR.Add(Math.Abs(mae) / (Math.Abs(e.Price - stop) / e.Price));
        }
        if (maeInR.Count == 0)
            return (false, "缺止损或 MAE 数据，无法核验回撤");
        var medMae = Median(maeInR);
        if (medMae > 1m)
            return (false, $"MAE 中位 {medMae:0.00}R > 1R（典型回撤超过止损距离）");

        return (true, $"波次 {reps.Count} · t={t:0.0} · 扣费后为正 {netPositive * 100:0}% · 中位超额 {medExcess * 100:+0.00;-0.00}% · 分半稳定 · MAE {medMae:0.00}R");
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
