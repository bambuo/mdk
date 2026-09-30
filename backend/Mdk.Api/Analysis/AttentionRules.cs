namespace Mdk.Api.Analysis;

/// <summary>
/// 关注度判定（**确定性**）：把"价格在结构的什么位置"翻译成"此刻是否值得看单"。
///
/// 规则来源为缠论的三类买卖点位置（见 <see cref="AttentionRules.Rules"/>，界面悬停展示）：
/// 只有三类位置值得关注——① 中枢边缘回抽不破（3 类，顺势）② 趋势末端背驰（1 类，逆势，最难）
/// ③ 一类后首次回抽不破（2 类）；其余位置（中枢内震荡、趋势中段追单区）不构成交易位置。
///
/// 判据全部由结构位置直接读出，不含概率推断；"关注"不等于"可交易"——
/// 本项目实测扣费后无统计优势，本判定只回答"结构上是否到了决策位"。
/// </summary>
public static class AttentionRules
{
    /// <summary>买卖点"新鲜度"上限（根）：超过则认为该位置已过。</summary>
    public const int FreshBars = 10;

    /// <summary>贴边阈值（ATR 倍数）：距中枢边界在此以内算"边缘"。 */
    public const decimal EdgeAtr = 0.5m;

    /// <summary>趋势中段阈值（ATR 倍数）：超过则认为已远离中枢，属追单区。 */
    public const decimal FarAtr = 2m;

    /// <summary>强背驰阈值（MACD 面积比）。</summary>
    public const decimal StrongDivergence = 0.7m;

    /// <summary>已走 R 上限：超过说明入场成本已吃掉一个风险单位。</summary>
    public const decimal MaxMovedR = 1m;

    /// <summary>五条规则速查（界面悬停展示，与文档同源）。</summary>
    public static readonly string[] Rules =
    [
        "① 结构链：包含处理 → 分型 → 笔 → 中枢 → 背驰 → 买卖点；笔≥3 构成中枢（多空成本区）",
        "② 只做三类位置：中枢边缘回抽不破（3类，顺势）／趋势末端背驰（1类，逆势最难）／一类后首次回抽（2类）",
        "③ 确认才作数：只有已确认的笔产生买卖点；信号记在确认那根K线，你看到时价格已离开参考点",
        "④ 止损=结构失效位：价格回到买卖点所依据的参考极值，前提即不成立（面板给出距离）",
        "⑤ 三不做：中枢内震荡不猜方向；距中枢 >2×ATR 的趋势中段不追；已走 >1R 不进场",
    ];

    /// <summary>关注度档位：focus=重点关注 / watch=可以关注 / wait=结构就位但等确认 / none=暂不关注 / chase=不追。</summary>
    public sealed record Verdict(
        string Level,
        string Headline,
        IReadOnlyList<string> Facts,
        string Why,
        string How,
        string? Dont);

    public static Verdict Evaluate(StructurePosition p)
    {
        var facts = new List<string>();
        if (p.PivotZg is not null && p.PivotZd is not null)
            facts.Add($"中枢 {p.PivotZd:0.##} ~ {p.PivotZg:0.##}（{p.PivotStrokes} 笔）");
        facts.Add(p.PivotZone switch
        {
            "above" => $"价格在中枢上方 距上沿 {Atr(p.EdgeDistanceAtr, p.EdgeDistancePct)}",
            "below" => $"价格在中枢下方 距下沿 {Atr(p.EdgeDistanceAtr, p.EdgeDistancePct)}",
            "in" => "价格在中枢内（震荡区）",
            _ => "窗口内无中枢"
        });
        if (p.LastKind is not null || p.MovedR is not null)
        {
            var bits = new List<string>();
            if (p.LastKind is not null) bits.Add($"最近买卖点 {p.LastKind}");
            if (p.BarsSinceLastSignal is { } b) bits.Add(b == 0 ? "本根" : $"{b} 根前");
            if (p.MovedR is { } m) bits.Add($"已走 {m:0.00}R");
            facts.Add(string.Join(" · ", bits));
        }
        if (p.DistanceToStopReferencePct is { } d) facts.Add($"距止损参考 {d * 100m:0.##}%");

        // ── 判定顺序：先排除"不可追"，再看是否有新鲜的三类位置 ──
        if (p.PivotZone == "in")
            return new Verdict("none", "暂不关注：价格在中枢内（震荡区）", facts,
                "中枢内是多空成本区，方向未定；此时猜方向属逆结构操作。",
                "等价格离开中枢（上/下沿）并回抽不破，再看是否形成 3 类买卖点。",
                null);

        if (p.PivotZone is "above" or "below" && p.EdgeDistanceAtr is { } atr && atr > FarAtr)
            return new Verdict("chase", "不追：已离开中枢较远（趋势中段）", facts,
                "距中枢超过 2×ATR 时，既不是回抽位也不是背驰位，属趋势中段——追单亏损最集中的区域。",
                "等价格回抽到中枢边缘（≤0.5×ATR）再看；或等新的买卖点形成。",
                p.MovedR is { } mr && mr > MaxMovedR ? $"且已走 {mr:0.00}R，入场成本已吃掉一个风险单位。" : null);

        var fresh = p.BarsSinceLastSignal is { } bars && bars <= FreshBars;
        var movedTooFar = p.MovedR is { } m2 && m2 > MaxMovedR;
        var edge = p.EdgeDistanceAtr is { } a2 && a2 <= EdgeAtr;

        if (fresh && p.LastKind is "3买" or "3卖" && edge)
            return new Verdict("focus", $"重点关注：中枢边缘回抽（{p.LastKind}）", facts,
                "价格离开中枢后回抽不回中枢，等于否定震荡、确认趋势延续——缠论里唯一顺势合法的入场位，且止损极明确。",
                "止损放结构失效位（面板给出距离），目标空间至少 3R 才值得（费率占 0.2%）。",
                movedTooFar ? $"已走 {p.MovedR:0.00}R，成本已吃掉一个风险单位——此位置不宜再进。" : null);

        if (fresh && p.LastKind is "1买" or "1卖")
        {
            var strong = p.LastAreaRatio is { } ar && ar <= StrongDivergence;
            return new Verdict("watch", $"可以关注：趋势末端背驰（{p.LastKind}{(strong ? $"，面积比 {p.LastAreaRatio:0.00}" : "，背驰偏弱")}）", facts,
                "1 类是最难的一类：需两个以上同向中枢后的背驰才算趋势末端；本项目样本里 1 类表现最差（逆势位）。",
                "等第 2 类确认再动手——即背驰后首次回抽不破前低/前高。",
                movedTooFar ? $"已走 {p.MovedR:0.00}R，成本已高。" : null);
        }

        if (fresh && p.LastKind is "2买" or "2卖")
            return new Verdict("watch", $"可以关注：一类后回抽确认（{p.LastKind}）", facts,
                "2 类风险低于 1 类（前低/前高已被验证），但空间也相应更小。",
                "止损放结构失效位；本项目该类别样本量最少，判据本身尚不充分。",
                movedTooFar ? $"已走 {p.MovedR:0.00}R，成本已高。" : null);

        return new Verdict("wait", "结构就位，等确认", facts,
            edge
                ? "价格已贴中枢边缘，但尚无新鲜的买卖点——位置对了，触发条件没到。"
                : "价格在中枢外侧但未贴边，位置尚未到位。",
            "等回抽至中枢边缘（≤0.5×ATR）并出现已确认的买卖点；未确认的笔不构成位置。",
            movedTooFar ? $"已走 {p.MovedR:0.00}R，成本已高。" : null);
    }

    private static string Atr(decimal? atrMultiple, decimal? pct) =>
        atrMultiple is { } a ? $"{a:0.0}×ATR" : pct is { } p ? $"{p * 100m:0.##}%" : "—";
}
