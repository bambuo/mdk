namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论第三步：由分型构建笔。
///
/// 口径唯一化的理由：新老笔属流派分歧，本项目择新笔实现（AGENTS.md）。
/// 曾提供 StrokeMode 配置旋钮切换，但老笔分支从未验证、取值亦无校验（拼错即静默回落），
/// 一旦误改会让全系统的笔/中枢/买卖点静默换口径 —— 故移除旋钮（2026-09-30 用户拍板）。
/// 新笔（本项目**唯一口径**）：两个分型的中间合并K线之间至少间隔 2 根独立合并K线。
///
/// 处理规则：
/// ① 顶底交替，同型分型只保留更极端者；
/// ② 间隔不足的异型分型：若它比"上一个同型分型"更极端，则用它收窄当前结构，否则丢弃；
/// ③ 结构收窄后若与更早分型间隔不足，向后回退合并（保持严格交替）。
///
/// 稳定性：尾部两笔仍可能被后续数据修订（缠论的笔延伸/破坏），因此只有身后至少
/// StabilityMarginStrokes 笔的笔才标记 IsConfirmed=true；StableFromBarIndex 记录其"不可修正"时点。
/// </summary>
public static class ChanStrokeBuilder
{
    /// <summary>新笔口径：两分型的中间合并K线之间至少间隔 2 根独立合并K线（口径唯一，不可切换）。</summary>
    public const int MinMergedBarsBetween = 2;

    /// <summary>身后至少这么多笔，当前笔才视为不可修正。</summary>
    public const int StabilityMarginStrokes = 2;

    public static IReadOnlyList<ChanStroke> Build(
        IReadOnlyList<ChanFractal> fractals,
        int minMergedBarsBetween = MinMergedBarsBetween,
        int stabilityMarginStrokes = StabilityMarginStrokes)
    {
        var pts = BuildAlternatingSequence(fractals, minMergedBarsBetween);

        var strokes = new List<ChanStroke>();
        for (var i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            // 底→顶为上升笔
            var isUp = !a.IsTop;

            // 该笔可被后续数据修订，直到身后累积足够笔数
            var stableStrokeIndex = i - 1;
            var lastIndex = pts.Count - 2; // 最后一笔的序号
            var isConfirmed = stableStrokeIndex + stabilityMarginStrokes <= lastIndex;
            int? stableFrom = null;
            if (isConfirmed)
            {
                var stabilizer = pts[stableStrokeIndex + stabilityMarginStrokes + 1];
                stableFrom = stabilizer.ConfirmBarIndex;
            }

            strokes.Add(new ChanStroke(
                StartBarIndex: a.BarIndex,
                StartPrice: a.Price,
                EndBarIndex: b.BarIndex,
                EndPrice: b.Price,
                IsUp: isUp,
                IsConfirmed: isConfirmed,
                StableFromBarIndex: stableFrom));
        }
        return strokes;
    }

    /// <summary>构建严格交替的分型序列（含间隔约束与回退）。</summary>
    private static List<ChanFractal> BuildAlternatingSequence(IReadOnlyList<ChanFractal> fractals, int minMergedBarsBetween)
    {
        var pts = new List<ChanFractal>();
        foreach (var f in fractals)
        {
            if (pts.Count == 0)
            {
                pts.Add(f);
                continue;
            }

            if (pts[^1].IsTop == f.IsTop)
            {
                // 同型：保留更极端者
                if (IsMoreExtreme(f, pts[^1]))
                {
                    pts[^1] = f;
                    EnforceGapBackwards(pts, minMergedBarsBetween);
                }
                continue;
            }

            if (GapOk(pts[^1], f, minMergedBarsBetween))
            {
                pts.Add(f);
                continue;
            }

            // 间隔不足：用更极端者收窄结构，否则丢弃
            if (pts.Count >= 2)
            {
                var prev = pts[^2];
                if (IsMoreExtreme(f, prev))
                {
                    pts.RemoveRange(pts.Count - 2, 2);
                    pts.Add(IsMoreExtreme(f, prev) ? f : prev);
                    EnforceGapBackwards(pts, minMergedBarsBetween);
                }
            }
        }
        return pts;
    }

    /// <summary>
    /// 收窄后回退：与更早分型间隔不足时，把末尾两个分型合并为一个更极端者，维持严格交替。
    /// 序列严格交替，故去掉末两个后，新的末尾（原 ^3）必然与 `last` 同型——只需在两者间取更极端者；
    /// 若列表已空则直接放回二者中更极端的一个。
    /// </summary>
    private static void EnforceGapBackwards(List<ChanFractal> pts, int minMergedBarsBetween)
    {
        while (pts.Count >= 2 && !GapOk(pts[^2], pts[^1], minMergedBarsBetween))
        {
            var last = pts[^1];
            var prev = pts[^2];
            pts.RemoveRange(pts.Count - 2, 2);

            if (pts.Count == 0)
                pts.Add(IsMoreExtreme(last, prev) ? last : prev);
            else if (IsMoreExtreme(last, pts[^1]))
                pts[^1] = last;   // 新的末尾与 last 同型（严格交替），保留更极端者
        }
    }

    private static bool GapOk(ChanFractal a, ChanFractal b, int minMergedBarsBetween) =>
        b.MergedIndex - a.MergedIndex - 1 >= minMergedBarsBetween;

    private static bool IsMoreExtreme(ChanFractal candidate, ChanFractal reference) =>
        candidate.IsTop ? candidate.Price > reference.Price : candidate.Price < reference.Price;
}
