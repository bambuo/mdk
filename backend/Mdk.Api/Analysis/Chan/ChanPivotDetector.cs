namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论第四步：笔中枢。连续三笔存在重叠区间即构成中枢：
/// Zg = 三笔高点的最小值，Zd = 三笔低点的最大值，要求 Zg > Zd。
///
/// 延伸与离开（关键：笔是首尾相连的，因此不能用"整笔范围是否相交"判断，否则任何离开笔都必然相交）：
/// · 延伸：后续笔的**终点**仍在 [Zd, Zg] 内 → 并入（StrokeCount 递增）；
/// · 结束：某笔终点离开区间，且**再下一笔也未回到区间内** → 中枢结束，该笔为"离开笔"；
///   若再下一笔回到区间内，则视为中枢震荡继续延伸。
/// IsConfirmed 表示中枢已被离开笔确认（不再延伸）。
/// </summary>
public static class ChanPivotDetector
{
    public static IReadOnlyList<ChanPivot> Detect(IReadOnlyList<ChanStroke> strokes)
    {
        var pivots = new List<ChanPivot>();
        var i = 0;
        while (i + 2 < strokes.Count)
        {
            var zg = Math.Min(Math.Min(strokes[i].High, strokes[i + 1].High), strokes[i + 2].High);
            var zd = Math.Max(Math.Max(strokes[i].Low, strokes[i + 1].Low), strokes[i + 2].Low);
            if (zg <= zd)
            {
                i++; // 三笔无重叠，窗口右移
                continue;
            }

            var end = i + 2;
            int? leaving = null;
            while (end + 1 < strokes.Count)
            {
                if (EndInside(strokes[end + 1], zd, zg))
                {
                    end++; // 终点仍在区间内 → 延伸
                    continue;
                }
                // 终点离开区间：若再下一笔回到区间内，视为震荡继续
                if (end + 2 < strokes.Count && EndInside(strokes[end + 2], zd, zg))
                {
                    end += 2;
                    continue;
                }
                leaving = end + 1;
                break;
            }

            pivots.Add(new ChanPivot(
                Zg: zg,
                Zd: zd,
                StartStrokeIndex: i,
                EndStrokeIndex: end,
                StrokeCount: end - i + 1,
                IsConfirmed: leaving.HasValue,
                LeavingStrokeIndex: leaving));

            i = leaving ?? end + 1;
        }
        return pivots;
    }

    private static bool EndInside(ChanStroke stroke, decimal zd, decimal zg) =>
        stroke.EndPrice >= zd && stroke.EndPrice <= zg;
}
