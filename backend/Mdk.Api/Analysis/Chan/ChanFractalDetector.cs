using Mdk.Api.Models;

namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论第二步：在合并K线上识别顶/底分型。
/// 顶分型：中间合并K线的高点与低点都高于左右两根；底分型反之。
/// 连续同类分型只保留更极端者（顶取更高、底取更低）。
/// 每个分型记录 ConfirmBarIndex（右邻合并K线最后一根原始K线的索引）——分型在该K线收盘后才可知。
/// </summary>
public static class ChanFractalDetector
{
    public static IReadOnlyList<ChanFractal> Detect(IReadOnlyList<MergedCandle> merged, IReadOnlyList<Candle> candles)
    {
        var raw = new List<ChanFractal>();
        for (var i = 1; i < merged.Count - 1; i++)
        {
            var prev = merged[i - 1];
            var mid = merged[i];
            var next = merged[i + 1];

            var isTop = mid.High > prev.High && mid.High > next.High
                        && mid.Low > prev.Low && mid.Low > next.Low;
            var isBottom = mid.Low < prev.Low && mid.Low < next.Low
                           && mid.High < prev.High && mid.High < next.High;
            if (!isTop && !isBottom) continue;

            var barIndex = ExtremeBarIndex(candles, mid, isTop);
            var price = isTop ? mid.High : mid.Low;
            raw.Add(new ChanFractal(i, barIndex, price, isTop, next.EndIndex));
        }

        // 连续同型取极端（顶更高 / 底更低）
        var result = new List<ChanFractal>();
        foreach (var f in raw)
        {
            if (result.Count > 0 && result[^1].IsTop == f.IsTop)
            {
                var last = result[^1];
                var moreExtreme = f.IsTop ? f.Price > last.Price : f.Price < last.Price;
                if (moreExtreme) result[^1] = f;
                continue;
            }
            result.Add(f);
        }
        return result;
    }

    /// <summary>在合并K线覆盖的原始K线区间内，找出价格极值所在的那根原始K线。</summary>
    private static int ExtremeBarIndex(IReadOnlyList<Candle> candles, MergedCandle merged, bool isTop)
    {
        var best = merged.StartIndex;
        for (var i = merged.StartIndex; i <= merged.EndIndex && i < candles.Count; i++)
        {
            if (isTop ? candles[i].High > candles[best].High : candles[i].Low < candles[best].Low)
                best = i;
        }
        return best;
    }
}
