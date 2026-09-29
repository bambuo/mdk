namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 合并K线（包含关系处理后的K线）。StartIndex/EndIndex 指向原始K线区间，用于回映射时间与价格极值。
/// IsUp：该合并K线相对前一根的方向（决定包含处理取"高高"还是"低低"）。
/// </summary>
public readonly record struct MergedCandle(int StartIndex, int EndIndex, double High, double Low, bool IsUp);

/// <summary>
/// 分型。BarIndex 为区间内价格极值所在原始K线；ConfirmBarIndex 为该分型可知的原始K线索引
/// （右邻合并K线收盘后才可能判断出分型，用于严格无未来函数）。
/// </summary>
public readonly record struct ChanFractal(int MergedIndex, int BarIndex, double Price, bool IsTop, int ConfirmBarIndex);

/// <summary>
/// 笔（新笔）。IsUp=true 表示底→顶的上升笔。IsConfirmed 表示该笔已不可被后续数据修正
/// （缠论的笔存在延伸/回退，只有尾部若干笔可被修订）。StableFromBarIndex 为进入"不可修正"状态的原始K线索引。
/// </summary>
public readonly record struct ChanStroke(
    int StartBarIndex,
    double StartPrice,
    int EndBarIndex,
    double EndPrice,
    bool IsUp,
    bool IsConfirmed,
    int? StableFromBarIndex)
{
    public double High => Math.Max(StartPrice, EndPrice);

    public double Low => Math.Min(StartPrice, EndPrice);

    /// <summary>该笔覆盖的原始K线区间（含端点）。</summary>
    public int BarSpan => EndBarIndex - StartBarIndex;
}

/// <summary>
/// 笔中枢：连续三笔重叠区间 [Zd, Zg]。StartStrokeIndex/EndStrokeIndex 为构成中枢的笔序号区间（含），
/// LeavingStrokeIndex 为离开中枢的笔序号（中枢已结束时才有值）。
/// </summary>
public readonly record struct ChanPivot(
    double Zg,
    double Zd,
    int StartStrokeIndex,
    int EndStrokeIndex,
    int StrokeCount,
    bool IsConfirmed,
    int? LeavingStrokeIndex);

/// <summary>背驰：面积比 &lt; 1 表示离开段力度弱于进入段（创新高/新低但面积缩小）。</summary>
public readonly record struct ChanDivergence(bool IsUp, double AreaRatio, double EnterArea, double LeaveArea);

/// <summary>
/// 缠论买卖点。Time/Price 为**记账口径**（结构确认那根K线的收盘），
/// ReferencePrice 为结构参考极值（如 1 类买点对应的笔低点），仅写在说明里，不用于记账。
/// StopPrice = 结构失效位 ± 0.5×ATR；若确认时价格已越过该失效位（止损落到入场价反向侧），
/// 该信号不再可交易，会被直接丢弃而非输出。
/// </summary>
public sealed record ChanBuySellPoint(
    string Kind,
    string Side,
    long Time,
    double Price,
    double ReferencePrice,
    int ReferenceBarIndex,
    double StopPrice,
    double? AreaRatio,
    string Note);

/// <summary>
/// 缠论分析结果（结构 + 买卖点 + 图表序列）。
/// LevelMode：stroke=以笔为最小单位（笔中枢，默认）；segment=启用线段（线段中枢与线段级别买卖点）。
/// Segments 在 stroke 模式下为空。
/// </summary>
public sealed record ChanResult(
    IReadOnlyList<ChanFractal> Fractals,
    IReadOnlyList<ChanStroke> Strokes,
    IReadOnlyList<ChanPivot> Pivots,
    IReadOnlyList<ChanBuySellPoint> Points,
    IReadOnlyDictionary<string, double?[]> Series,
    IReadOnlyList<ChanSegment>? Segments = null,
    string LevelMode = "stroke");
