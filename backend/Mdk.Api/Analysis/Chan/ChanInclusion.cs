using Mdk.Api.Models;

namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论第一步：K线包含关系处理（合并K线）。
/// 存在包含关系（一根完全覆盖另一根，含等高等价的平台）时按方向合并：
/// 向上处理取"高高"（两者最高取高、最低取高），向下处理取"低低"。
/// 与 SupportResistance.FindSwings 的 ±3 严格分形不同，此处按缠论规则正确处理平台/等高价，
/// 且不做左右对称的未来确认（确认时点由分型的 ConfirmBarIndex 显式表达）。
/// </summary>
public static class ChanInclusion
{
    public static IReadOnlyList<MergedCandle> Merge(IReadOnlyList<Candle> candles)
    {
        var merged = new List<MergedCandle>(candles.Count);
        for (var i = 0; i < candles.Count; i++)
        {
            var bar = candles[i];
            if (merged.Count == 0)
            {
                merged.Add(new MergedCandle(i, i, bar.High, bar.Low, true));
                continue;
            }

            var last = merged[^1];
            var contains =
                (bar.High <= last.High && bar.Low >= last.Low) ||
                (bar.High >= last.High && bar.Low <= last.Low);

            if (contains)
            {
                double high, low;
                if (last.IsUp)
                {
                    high = Math.Max(last.High, bar.High);
                    low = Math.Max(last.Low, bar.Low);
                }
                else
                {
                    high = Math.Min(last.High, bar.High);
                    low = Math.Min(last.Low, bar.Low);
                }
                merged[^1] = last with { High = high, Low = low, EndIndex = i };
                continue;
            }

            // 方向由与前一根合并K线的关系决定：创新高为向上，否则向下
            var isUp = bar.High > last.High;
            merged.Add(new MergedCandle(i, i, bar.High, bar.Low, isUp));
        }
        return merged;
    }
}
