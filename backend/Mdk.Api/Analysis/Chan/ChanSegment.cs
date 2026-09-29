namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 线段：由至少三笔构成，且只能被"线段"破坏（不能用笔破坏）。
/// StartStrokeIndex/EndStrokeIndex 为构成该线段的笔序号区间（含端点）；
/// StartBarIndex/EndBarIndex 为对应的原始K线索引，用于绘图与时间映射。
/// IsConfirmed=false 表示该线段仍可能被后续走势延伸/修正（尾部线段）。
/// StableFromBarIndex = 该线段"不可再被修正"的原始K线索引（即破坏被确认的那根K线）。
/// </summary>
public readonly record struct ChanSegment(
    int StartStrokeIndex,
    int EndStrokeIndex,
    int StartBarIndex,
    decimal StartPrice,
    int EndBarIndex,
    decimal EndPrice,
    bool IsUp,
    bool IsConfirmed,
    int? StableFromBarIndex)
{
    public decimal High => Math.Max(StartPrice, EndPrice);

    public decimal Low => Math.Min(StartPrice, EndPrice);

    public int StrokeCount => EndStrokeIndex - StartStrokeIndex + 1;

    /// <summary>转为"类笔"结构，便于复用笔中枢算法与信号规则（线段中枢 = 三线段重叠）。</summary>
    public ChanStroke AsStroke() => new(
        StartBarIndex, StartPrice, EndBarIndex, EndPrice, IsUp, IsConfirmed, StableFromBarIndex);
}
