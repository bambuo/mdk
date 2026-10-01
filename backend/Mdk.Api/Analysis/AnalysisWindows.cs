using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>
/// 窗口选择：把"调用方请求的显示窗口"与"结构与位点的固定内部窗口"分开。
///
/// 为什么必须分开：缠论中枢与位点聚类都是**顺序结构**——窗口一变划分就变。
/// 若在显示窗口上计算，同一个标的在 limit=300 / 500 / 1000 下会得到不同的中枢与位点，
/// 即"结构随窗口漂移"，属口径未定（同一段行情必须给出相同结果）。
/// 内部窗口固定取最近 N 根，因此与调用方 limit 无关。
/// </summary>
public static class AnalysisWindows
{
    /// <summary>
    /// 三个窗口都以**同一根K线结束**（末段对齐）：
    /// Display=调用方显示的根数；Chan=缠论内部固定根数；Levels=位点内部固定根数。
    /// </summary>
    public readonly record struct Selection(
        IReadOnlyList<Candle> Display,
        IReadOnlyList<Candle> Chan,
        IReadOnlyList<Candle> Levels);

    public static Selection Select(IReadOnlyList<Candle> fetched, int displayLimit, int chanBars, int levelBars) =>
        new(Tail(fetched, Math.Max(1, displayLimit)),
            Tail(fetched, Math.Max(1, chanBars)),
            Tail(fetched, Math.Max(1, levelBars)));

    /// <summary>取末尾 count 根（不足则全部）。</summary>
    public static IReadOnlyList<Candle> Tail(IReadOnlyList<Candle> items, int count)
    {
        if (items.Count <= count) return items;
        return items switch
        {
            Candle[] array => array[(array.Length - count)..],
            List<Candle> list => list.GetRange(list.Count - count, count),
            _ => items.Skip(items.Count - count).ToList(),
        };
    }
}
