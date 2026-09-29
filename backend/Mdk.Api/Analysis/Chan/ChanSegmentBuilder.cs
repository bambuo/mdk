namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 线段构建（特征序列法）。
///
/// 规则与口径（按原文，个别处按可实现的明确口径落地）：
/// ① 线段由**至少三笔**构成；且只有"特征序列分型"成立才算被破坏（单笔不构成破坏）；
/// ② 上升线段的**特征序列** = 其内部的向下笔（反向笔）依次排列；下降线段取向上笔；
/// ③ 特征序列元素之间做**包含处理**（方向由前两个元素的高点关系决定，与K线包含处理同构）；
/// ④ 破坏判定看特征序列**分型**：上升线段需顶分型（中间元素的高、低都高于左右），下降线段需底分型；
/// ⑤ **第一种破坏**：分型左、中元素之间**无缺口**（左元素高点 ≥ 中元素低点）→ 分型成立即破坏；
///    **第二种破坏**：存在缺口 → 需后续特征序列元素**回补缺口**（低点回到左元素高点之下）才确认；
///    期间若价格创出超过分型中元素的新高（新低），说明趋势延续 → 候选分型作废、线段继续延伸；
/// ⑥ 破坏确认后：线段终点 = 分型中间元素之前那一笔的终点（该线段的极值点）；下一线段从中间元素所在笔开始
///    （线段首尾相连、方向交替）。
///
/// 无未来函数：StableFromBarIndex = 破坏被确认的那根K线索引（缺口情形为回补缺口的那根）。
/// </summary>
public static class ChanSegmentBuilder
{
    /// <summary>线段最少笔数。</summary>
    public const int MinStrokes = 3;

    /// <summary>特征序列元素（由反向笔构成，做包含处理后可能覆盖多笔）。</summary>
    private sealed record Element(double High, double Low, int StrokeIndex, int EndBarIndex);

    public static IReadOnlyList<ChanSegment> Build(IReadOnlyList<ChanStroke> strokes)
    {
        var segments = new List<ChanSegment>();
        if (strokes.Count < MinStrokes) return segments;

        var start = 0;
        while (start + MinStrokes - 1 < strokes.Count)
        {
            var (broken, midStroke, confirmBar) = FindBreak(strokes, start);

            if (!broken)
            {
                // 尾部未破坏：作为"未确认线段"（可能被后续数据延伸/修正），不产生信号
                segments.Add(CreateSegment(strokes, start, strokes.Count - 1, isConfirmed: false, stableFrom: null));
                break;
            }

            var endStroke = midStroke - 1;
            if (endStroke < start + MinStrokes - 1)
            {
                // 分型形成时线段不足三笔（理论不应发生），保护性退出
                segments.Add(CreateSegment(strokes, start, strokes.Count - 1, isConfirmed: false, stableFrom: null));
                break;
            }

            segments.Add(CreateSegment(strokes, start, endStroke, isConfirmed: true, stableFrom: confirmBar));
            start = midStroke;   // 下一线段从分型中间元素所在笔开始（首尾相连）
        }
        return segments;
    }

    /// <summary>尝试找出从 start 笔开始的线段的破坏点；返回 (是否破坏, 分型中间元素所在笔, 确认K线索引)。</summary>
    private static (bool Broken, int MidStroke, int ConfirmBar) FindBreak(IReadOnlyList<ChanStroke> strokes, int start)
    {
        var dir = strokes[start].IsUp;
        var elements = new List<Element>();
        Element? pendingLeft = null;
        Element? pendingMid = null;

        for (var k = start + 1; k < strokes.Count; k += 2)   // 特征序列 = 反向笔
        {
            AddElement(elements, strokes, k, dir);

            var last = elements[^1];

            // ① 已存在"有缺口"的候选分型：先看缺口是否回补、或趋势是否延续使其作废
            if (pendingMid is not null && pendingLeft is not null)
            {
                var closed = dir ? last.Low <= pendingLeft.High : last.High >= pendingLeft.Low;
                if (closed) return (true, pendingMid.StrokeIndex, last.EndBarIndex);
                var trendExtended = dir ? last.High > pendingMid.High : last.Low < pendingMid.Low;
                if (trendExtended)
                {
                    pendingMid = null;   // 趋势延续 → 候选作废，线段继续延伸
                    pendingLeft = null;
                }
                continue;
            }

            // ② 尾部三元素是否构成分型
            if (elements.Count < 3) continue;
            var left = elements[^3];
            var mid = elements[^2];
            var right = elements[^1];

            var isFractal = dir
                ? mid.High > left.High && mid.High > right.High && mid.Low > left.Low && mid.Low > right.Low
                : mid.Low < left.Low && mid.Low < right.Low && mid.High < left.High && mid.High < right.High;
            if (!isFractal) continue;

            var hasGap = dir ? left.High < mid.Low : left.Low > mid.High;
            if (!hasGap)
            {
                // 第一种破坏：无缺口 → 立即确认
                return (true, mid.StrokeIndex, right.EndBarIndex);
            }

            // 第二种破坏：记录候选，等待后续元素回补缺口
            pendingLeft = left;
            pendingMid = mid;
        }

        return (false, -1, -1);
    }

    private static ChanSegment CreateSegment(
        IReadOnlyList<ChanStroke> strokes, int startStroke, int endStroke, bool isConfirmed, int? stableFrom)
    {
        var first = strokes[startStroke];
        var last = strokes[endStroke];
        return new ChanSegment(
            StartStrokeIndex: startStroke,
            EndStrokeIndex: endStroke,
            StartBarIndex: first.StartBarIndex,
            StartPrice: first.StartPrice,
            EndBarIndex: last.EndBarIndex,
            EndPrice: last.EndPrice,
            IsUp: first.IsUp,
            IsConfirmed: isConfirmed,
            StableFromBarIndex: stableFrom);
    }

    /// <summary>
    /// 追加特征序列元素并做包含处理（与K线包含处理同构）：
    /// 方向由"前一个元素相对更前一个元素"的高点关系决定；包含时向上取高低高、向下取低低。
    /// 合并后的元素保留**较后笔**的序号与结束K线索引（用于确认时点与时间映射）。
    /// </summary>
    private static void AddElement(List<Element> elements, IReadOnlyList<ChanStroke> strokes, int strokeIndex, bool segmentIsUp)
    {
        var stroke = strokes[strokeIndex];
        var next = new Element(stroke.High, stroke.Low, strokeIndex, stroke.EndBarIndex);
        if (elements.Count == 0)
        {
            elements.Add(next);
            return;
        }

        var last = elements[^1];
        var contains = (next.High <= last.High && next.Low >= last.Low)
                       || (next.High >= last.High && next.Low <= last.Low);
        if (!contains)
        {
            elements.Add(next);
            return;
        }

        var up = elements.Count switch
        {
            >= 3 => elements[^2].High > elements[^3].High,
            2 => elements[^1].High > elements[^2].High,
            _ => segmentIsUp,
        };
        double high, low;
        if (up)
        {
            high = Math.Max(last.High, next.High);
            low = Math.Max(last.Low, next.Low);
        }
        else
        {
            high = Math.Min(last.High, next.High);
            low = Math.Min(last.Low, next.Low);
        }
        elements[^1] = new Element(high, low, strokeIndex, stroke.EndBarIndex);
    }
}
