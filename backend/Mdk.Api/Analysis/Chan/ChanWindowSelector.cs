using Mdk.Api.Models;

namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 缠论窗口选择：把"调用方请求的显示窗口"与"缠论内部固定窗口"分开。
/// 内部窗口始终取最近 analysisBars 根（可用不足时取全部），因此与调用方 limit 无关——
/// 这是缠论结构与信号可复现的前提（中枢划分是顺序结构，窗口一变划分就变）。
/// </summary>
public static class ChanWindowSelector
{
    /// <summary>返回 (显示窗口, 缠论内部窗口)；两个窗口都以同一根K线结束。</summary>
    public static (IReadOnlyList<Candle> Display, IReadOnlyList<Candle> ChanWindow) Select(
        IReadOnlyList<Candle> fetched, int displayLimit, int analysisBars)
    {
        var display = fetched.Count > displayLimit ? Tail(fetched, displayLimit) : fetched;
        var chanBars = Math.Max(1, analysisBars);
        var chanWindow = fetched.Count > chanBars ? Tail(fetched, chanBars) : fetched;
        return (display, chanWindow);
    }

    private static IReadOnlyList<Candle> Tail(IReadOnlyList<Candle> items, int count) =>
        items switch
        {
            Candle[] array => array[(array.Length - count)..],
            List<Candle> list => list.GetRange(list.Count - count, count),
            _ => items.Skip(items.Count - count).ToList(),
        };
}
