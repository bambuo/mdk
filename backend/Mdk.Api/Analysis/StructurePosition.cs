using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

// 某个级别的"价格在结构中的位置"（确定性描述，不含任何概率/统计推断）。
public sealed record LevelPosition(
    // 级别标识：higher / primary / lower。
    string Level,
    string Interval,
    // 中枢归属：above=中枢上方（离开段）/ in=中枢内（震荡）/ below=中枢下方 / none=无中枢。
    string Zone,
    // 距中枢上沿（zone=above 时）或下沿（zone=below 时）的距离占价格比例。
    decimal? DistancePct,
    decimal? PivotZg,
    decimal? PivotZd,
    int PivotStrokes);

/// <summary>
/// 价格在缠论结构中的位置——**确定性输出**：给定同一段行情，任何时候、任何调用方得到同一结果。
/// 设计立场（2026-09-30 用户拍板）：本系统以结构分析为本分，回答"价格处在结构的哪里"，
/// 而不是预测涨跌或给出交易授权。所有字段都可由 K 线与结构直接重算，无概率假设。
/// </summary>
public sealed record StructurePosition(
    // 本级别当前笔方向：up / down / none。
    string StrokeDirection,
    // 当前笔是否已确认（未确认 = 仍在延伸，终点可能推进）。
    bool StrokeConfirmed,
    // 当前笔起点价 → 终点价。
    decimal? StrokeFrom,
    decimal? StrokeTo,
    // 价格自本笔极值端回撤的幅度 ÷ 本笔幅度（0=仍在极值端，1=回到起点；负数=已越过端点继续延伸）。
    decimal? RetracePct,
    // 本笔自起点分型已运行多少根K线。
    int StrokeBars,
    // 中枢归属（本级别）：above / in / below / none。
    string PivotZone,
    decimal? PivotZg,
    decimal? PivotZd,
    // 中枢已包含多少笔（越大越"老"，延伸程度）。
    int PivotStrokes,
    // 中枢自首笔起点已运行多少根K线。
    int PivotBars,
    // 价格距中枢边界（落在哪侧取哪侧）的距离占价格比例。
    decimal? EdgeDistancePct,
    // 同上，但以 ATR 归一（"离中枢上沿 0.8×ATR"比百分比更跨标的可比）。
    decimal? EdgeDistanceAtr,
    // 结构失效位：最近买卖点所依据的参考极值（价格回到该位，结构前提即不成立）。
    decimal? InvalidationPrice,
    // 止损参考：结构失效位上加减 ATR 缓冲后的可执行价位（≠ 失效位）。
    decimal? StopReferencePrice,
    // 现价距**止损参考**的距离占价格比例——即"1R 有多远"。
    decimal? DistanceToStopReferencePct,
    // 最近买卖点类别 / 距今根数（事实陈述，不含绩效推断）。
    string? LastKind,
    int? BarsSinceLastSignal,
    // 最近买卖点的记账价（入场参考）。
    decimal? LastSignalPrice,
    // 最近买卖点的背驰面积比（仅 1/2 类有值；≤0.7 属强背驰）。
    decimal? LastAreaRatio,
    // 已走 R：现价相对最近买卖点入场价走过的幅度 ÷ 该信号的风险单位（&gt;1 说明成本已吃掉一个风险单位）。
    decimal? MovedR,
    // 各级别中枢归属对照（高周期 / 本级别 / 次级别，缺数据则无该项）。
    IReadOnlyList<LevelPosition> Levels,
    // 多级别归属是否一致：aligned=各级别同侧 / mixed=分歧 / single=只有本级别有中枢。
    string CrossLevel,
    // 一句话人话描述（可直接展示）。
    string Summary,
    // 关注度判定（确定性）：把结构位置翻译为"此刻是否值得看单"，见 <see cref="AttentionRules"/>。
    AttentionRules.Verdict? Attention = null);

/// <summary>
/// 结构位置计算（纯函数，可独立测试）。所有度量都由 K 线与缠论结构直接算出，不含统计推断。
/// </summary>
public static class StructurePositionCalculator
{
    public static StructurePosition Compute(
        IReadOnlyList<Candle> candles,
        Chan.ChanResult result,
        decimal?[] atr,
        IReadOnlyList<LevelPosition> levels)
    {
        var last = candles[^1].Close;
        var lastIndex = candles.Count - 1;

        // ── 当前笔：方向 / 终点 / 价格在笔内的位置 ──
        Chan.ChanStroke? strokeOrNull = result.Strokes.Count > 0 ? result.Strokes[^1] : null;
        var strokeFrom = strokeOrNull?.StartPrice;
        var strokeTo = strokeOrNull?.EndPrice;
        var strokeBars = 0;
        decimal? retrace = null;
        if (strokeOrNull is { } s)
        {
            var startIdx = s.StartBarIndex;
            strokeBars = startIdx >= 0 && startIdx <= lastIndex ? lastIndex - startIdx : 0;
            var span = Math.Abs(s.EndPrice - s.StartPrice);
            if (span > 0m)
            {
                // 上升笔：自高点回撤 (终点−现价)/幅度；下降笔：自低点回撤 (现价−终点)/幅度
                retrace = s.IsUp ? (s.EndPrice - last) / span : (last - s.EndPrice) / span;
            }
        }

        // ── 中枢归属：在/上/下 + 距离（% 与 ATR 倍数）──
        var pivot = result.Pivots.Count > 0 ? result.Pivots[^1] : (Chan.ChanPivot?)null;
        var zone = "none";
        decimal? edgeDistancePct = null;
        var pivotStrokes = 0;
        var pivotBars = 0;
        if (pivot is { } p)
        {
            pivotStrokes = p.StrokeCount;
            var startStroke = p.StartStrokeIndex >= 0 && p.StartStrokeIndex < result.Strokes.Count
                ? result.Strokes[p.StartStrokeIndex]
                : default;
            var startIdx = startStroke.StartBarIndex;
            pivotBars = startIdx >= 0 && startIdx <= lastIndex ? lastIndex - startIdx : 0;

            if (last >= p.Zd && last <= p.Zg) zone = "in";
            else if (last > p.Zg)
            {
                zone = "above";
                edgeDistancePct = (last - p.Zg) / last;
            }
            else
            {
                zone = "below";
                edgeDistancePct = (p.Zd - last) / last;
            }
        }

        decimal? edgeDistanceAtr = null;
        if (pivot is { } pp && edgeDistancePct is not null && atr.Length > lastIndex && atr[lastIndex] is { } a &&
            a > 0m)
        {
            var edge = last > pp.Zg ? pp.Zg : pp.Zd;
            edgeDistanceAtr = Math.Abs(last - edge) / a;
        }

        // ── 最近买卖点：结构失效位（参考极值）与止损参考（含 ATR 缓冲）分列，口径见 LANGUAGE.md §7 ──
        var lastPoint = result.Points.Count > 0 ? result.Points[^1] : null;
        decimal? invalidPrice = lastPoint?.ReferencePrice;
        decimal? stopRef = lastPoint?.StopPrice;
        decimal? stopDistance = stopRef is { } stop && last > 0 ? Math.Abs(last - stop) / last : null;
        var barsSince = 0;
        if (lastPoint is { } lp)
        {
            var idx = IndexOfTime(candles, lp.Time);
            if (idx >= 0) barsSince = lastIndex - idx;
        }

        // 已走 R：价格自最近买卖点入场后走过的幅度（以该信号的风险单位为分母）
        decimal? movedR = null;
        if (lastPoint is { } lp2 && stopRef is { } sr && last > 0m)
        {
            var risk = Math.Abs(lp2.Price - sr);
            if (risk > 0m) movedR = Math.Abs(last - lp2.Price) / risk;
        }

        var crossLevel = CrossLevelOf(levels, zone);
        var summary = Describe(strokeOrNull?.IsUp, strokeOrNull?.IsConfirmed ?? false, retrace,
            zone, pivot is not null, pivotStrokes, edgeDistanceAtr, edgeDistancePct,
            lastPoint?.Kind, barsSince, stopDistance, stopRef, invalidPrice, crossLevel);

        return new StructurePosition(
            StrokeDirection: strokeOrNull is not { } st ? "none" : st.IsUp ? "up" : "down",
            StrokeConfirmed: strokeOrNull?.IsConfirmed ?? false,
            StrokeFrom: strokeFrom,
            StrokeTo: strokeTo,
            RetracePct: retrace,
            StrokeBars: strokeBars,
            PivotZone: zone,
            PivotZg: pivot?.Zg,
            PivotZd: pivot?.Zd,
            PivotStrokes: pivotStrokes,
            PivotBars: pivotBars,
            EdgeDistancePct: edgeDistancePct,
            EdgeDistanceAtr: edgeDistanceAtr,
            InvalidationPrice: invalidPrice,
            StopReferencePrice: stopRef,
            DistanceToStopReferencePct: stopDistance,
            LastKind: lastPoint?.Kind,
            BarsSinceLastSignal: lastPoint is null ? null : barsSince,
            LastSignalPrice: lastPoint?.Price,
            LastAreaRatio: lastPoint?.AreaRatio,
            MovedR: movedR,
            Levels: levels,
            CrossLevel: crossLevel,
            Summary: summary);
    }

    // 各级别中枢归属是否一致（只比较有中枢的级别）。
    private static string CrossLevelOf(IReadOnlyList<LevelPosition> levels, string ownZone)
    {
        var zones = levels.Where(l => l.Zone != "none").Select(l => l.Zone).Distinct().ToList();
        if (zones.Count == 0) return ownZone == "none" ? "none" : "single";
        return zones.Count == 1 ? "aligned" : "mixed";
    }

    // 一句话描述（交易语言，不带预测口吻）。
    private static string Describe(
        bool? isUp, bool confirmed, decimal? retrace, string zone, bool hasPivot, int pivotStrokes,
        decimal? edgeAtr, decimal? edgePct, string? kind, int barsSince,
        decimal? stopDistancePct, decimal? stopRef, decimal? invalidPrice, string crossLevel)
    {
        var parts = new List<string>();

        parts.Add(isUp switch
        {
            true => confirmed ? "当前向上笔（已确认）" : "当前向上笔（未确认，可能延伸）",
            false => confirmed ? "当前向下笔（已确认）" : "当前向下笔（未确认，可能延伸）",
            _ => "尚无已成形笔"
        });

        if (retrace is { } r)
        {
            var pct = r * 100m;
            parts.Add(r switch
            {
                < 0m => $"价格已越过本笔端点 {Math.Abs(pct):0.#}%（结构在延伸）",
                > 1m => $"价格已越过本笔起点 {pct - 100m:0.#}%（全幅回撤，本笔或将被破坏）",
                _ => $"价格位于本笔 {(1 - r) * 100m:0.#}% 处（自极值端回撤 {pct:0.#}%）"
            });
        }

        if (hasPivot)
        {
            parts.Add(zone switch
            {
                "in" => $"在中枢内（震荡，已延伸 {pivotStrokes} 笔）",
                "above" => $"在中枢上方（离开段，距上沿 {edgePct * 100m:0.##}%" +
                           (edgeAtr is { } a ? $"，约 {a:0.#}×ATR）" : "）"),
                "below" => $"在中枢下方（离开段，距下沿 {edgePct * 100m:0.##}%" +
                           (edgeAtr is { } a ? $"，约 {a:0.#}×ATR）" : "）"),
                _ => "无中枢"
            });
        }

        if (kind is not null)
        {
            parts.Add(barsSince == 0 ? $"最近买卖点 {kind}（本根）" : $"最近买卖点 {kind}（{barsSince} 根前）");
        }

        if (stopDistancePct is { } sd)
        {
            parts.Add($"距其止损参考 {sd * 100m:0.##}%" +
                      (invalidPrice is { } inv ? $"（结构失效位 {inv:0.##}）" : ""));
        }

        parts.Add(crossLevel switch
        {
            "aligned" => "各级别中枢归属一致",
            "mixed" => "各级别中枢归属分歧",
            "single" => "仅本级别有中枢",
            _ => "各级别均无中枢"
        });

        return string.Join("；", parts);
    }

    private static int IndexOfTime(IReadOnlyList<Candle> candles, long time)
    {
        for (var i = candles.Count - 1; i >= 0; i--)
            if (candles[i].Time == time)
                return i;
        return -1;
    }
}
