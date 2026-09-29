namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论第五步：背驰（MACD 面积法）。
/// 对"进入段"（进入中枢前的同向笔）与"离开段"（离开中枢的笔）分别累加 MACD 柱面积（Σ|hist|），
/// 当离开段创出新极值（更低低点 / 更高高点）但面积明显缩小（面积比 &lt; 阈值）时判定背驰。
/// </summary>
public static class ChanDivergenceDetector
{
    /// <summary>计算某笔区间内的 MACD 柱面积（绝对值累加）。</summary>
    public static double Area(IReadOnlyList<ChanStroke> strokes, int strokeIndex, double[] macdHist)
    {
        if (strokeIndex < 0 || strokeIndex >= strokes.Count) return 0;
        var s = strokes[strokeIndex];
        var from = Math.Max(0, Math.Min(s.StartBarIndex, s.EndBarIndex));
        var to = Math.Min(macdHist.Length - 1, Math.Max(s.StartBarIndex, s.EndBarIndex));
        var sum = 0.0;
        for (var i = from; i <= to; i++)
        {
            var v = macdHist[i];
            if (!double.IsNaN(v)) sum += Math.Abs(v);
        }
        return sum;
    }

    /// <summary>
    /// 判断离开段相对"进入段"是否背驰。进入段取离开笔之前**最近一笔同向笔**
    /// （连续笔首尾相连，紧邻离开笔的前一笔必然与之反向，故需向前寻找同向笔；
    /// 若中枢之前存在同向笔则即为进入段，否则退化为中枢内同向笔，属盘整背驰）。
    /// </summary>
    public static ChanDivergence? Check(
        IReadOnlyList<ChanStroke> strokes,
        ChanPivot pivot,
        double[] macdHist,
        double areaRatioThreshold)
    {
        if (pivot.LeavingStrokeIndex is not { } leaveIndex) return null;
        var enterIndex = FindEnterStroke(strokes, leaveIndex);
        if (enterIndex is not { } enter) return null;

        var enterStroke = strokes[enter];
        var leave = strokes[leaveIndex];
        if (enterStroke.IsUp != leave.IsUp) return null; // 同向才可比

        // 必须创出新极值：向下笔创新低 / 向上笔创新高
        var newExtreme = leave.IsUp ? leave.High > enterStroke.High : leave.Low < enterStroke.Low;
        if (!newExtreme) return null;

        var enterArea = Area(strokes, enter, macdHist);
        var leaveArea = Area(strokes, leaveIndex, macdHist);
        if (enterArea <= 0) return null;

        var ratio = leaveArea / enterArea;
        if (ratio >= areaRatioThreshold) return null;

        return new ChanDivergence(leave.IsUp, ratio, enterArea, leaveArea);
    }

    /// <summary>向前寻找最近一笔同向笔作为进入段。</summary>
    private static int? FindEnterStroke(IReadOnlyList<ChanStroke> strokes, int leaveIndex)
    {
        for (var j = leaveIndex - 1; j >= 0; j--)
        {
            if (strokes[j].IsUp == strokes[leaveIndex].IsUp) return j;
        }
        return null;
    }
}
