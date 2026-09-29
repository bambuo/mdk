namespace Mdk.Api.Analysis.Chan;

/// <summary>缠论配置（appsettings "Chan" 节）。</summary>
public sealed class ChanOptions
{
    public const string SectionName = "Chan";

    public static ChanOptions Default { get; } = new();

    /// <summary>是否启用缠论分析。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>笔的定义：Loose=新笔（分型间至少间隔 2 根独立合并K线），Strict=老笔（至少 3 根）。</summary>
    public string StrokeMode { get; set; } = "Loose";

    /// <summary>背驰判定阈值：离开段 MACD 面积 / 进入段面积 低于该值视为背驰。</summary>
    public decimal DivergenceAreaRatio { get; set; } = 0.9m;

    /// <summary>返回给前端的最多买卖点数量（取最近者，防止超长窗口下响应过大）。</summary>
    public int MaxPoints { get; set; } = 50;

    /// <summary>
    /// 暖机隔离：结构起点落在分析窗口前这么多根以内的中枢不产生买卖点。
    /// 原因（2026-09-29 审查发现）：中枢划分是顺序结构，紧贴窗口起点的中枢其"成形三笔"可能不完整，
    /// 换一个窗口（丢几根最早的K线）就会得到完全不同的中枢区间 → 信号不可复现。
    /// </summary>
    public int WarmupBars { get; set; } = 30;

    /// <summary>
    /// 缠论内部窗口的固定根数（取最近这么多根，与调用方请求的显示窗口无关）。
    /// 中枢划分是顺序结构，内部窗口一变划分就会变；固定它才能保证同一段行情在不同调用方（不同 limit）
    /// 与不同时间点得到一致的结构与信号，也保证落库样本的规则可比性。
    /// 显示窗口长于该值时，超出部分不绘制缠论图层。
    /// </summary>
    public int AnalysisBars { get; set; } = 700;

    /// <summary>
    /// 是否要求"次级别确认"：本级别买卖点需次级别在**结构参考点之后**该窗口内完成一笔同向笔
    /// （即次级别在该处发生了转向确认）。窗口以次级别K线根数计。
    /// 缠论中本级别买卖点需要次级别结构配合；轻量实现用"次级别转向"表达。
    /// 关闭后恢复为不确认的旧口径（便于日后 A/B 对比）。
    /// </summary>
    public bool RequireSubLevelConfirm { get; set; } = true;

    /// <summary>次级别转向确认的允许延迟（次级别K线根数）：超过则视为次级别未确认。</summary>
    public int SubLevelConfirmWindowBars { get; set; } = 10;

    /// <summary>
    /// 级别共振窗口（高周期K线根数）：本级别信号前该窗口内若出现同向高周期缠论信号，标记为"共振"。
    /// 依据：回填样本探测显示窗口越紧越好（1×4h 优于 2×4h 优于 6×4h）。
    /// </summary>
    public int ConfluenceWindowBars { get; set; } = 1;

    /// <summary>
    /// 是否启用**线段**（完整缠论体系）：开启后中枢由线段构成（线段中枢 = 三线段重叠），
    /// 1/2/3 类买卖点也基于线段（段级背驰比较，替代笔级）；关闭则维持"笔中枢"机制。
    /// 线段按特征序列法构建（见 ChanSegmentBuilder），最小三笔、只能被线段破坏、含缺口两种破坏。
    /// </summary>
    public bool UseSegments { get; set; }


    /// <summary>
    /// 线段模式下的内部窗口根数（默认 1200）。
    /// 线段刻画的是"趋势腿"：一段可以跨越几十上百笔，窗口太短时线段的破坏点常常落在窗口之外，
    /// 导致"只剩一两条未确认线段、几乎没有线段中枢"。实测 700 根 → 3 段/0 中枢；1500 根 → 9 段/2 中枢。
    /// </summary>
    public int SegmentAnalysisBars { get; set; } = 1200;

    /// <summary>实际使用的内部窗口：线段模式自动放宽（各模式各自的窗口都是固定的，可复现性不受影响）。</summary>
    public int EffectiveAnalysisBars => UseSegments ? Math.Max(AnalysisBars, SegmentAnalysisBars) : AnalysisBars;

    /// <summary>是否计算并返回多级别结构（高周期/本级别/次级别同级叠加显示）。</summary>
    public bool MultiLevel { get; set; } = true;

    /// <summary>次级别结构所需的最少K线数（不足则只返回本级别与高周期）。</summary>
    public int LowerLevelMinBars { get; set; } = 300;

    /// <summary>复制一份（可覆盖个别开关）。用于"关闭次级别确认"等变体，避免手写复制导致字段漏配。</summary>
    public ChanOptions Clone(
        bool? requireSubLevelConfirm = null, bool? multiLevel = null, bool? useSegments = null) => new()
    {
        Enabled = Enabled,
        StrokeMode = StrokeMode,
        DivergenceAreaRatio = DivergenceAreaRatio,
        MaxPoints = MaxPoints,
        WarmupBars = WarmupBars,
        AnalysisBars = AnalysisBars,
        RequireSubLevelConfirm = requireSubLevelConfirm ?? RequireSubLevelConfirm,
        SubLevelConfirmWindowBars = SubLevelConfirmWindowBars,
        ConfluenceWindowBars = ConfluenceWindowBars,
        MultiLevel = multiLevel ?? MultiLevel,
        UseSegments = useSegments ?? UseSegments,
        SegmentAnalysisBars = SegmentAnalysisBars,
        LowerLevelMinBars = LowerLevelMinBars,
    };

    public int MinMergedBarsBetween => StrokeMode.Equals("Strict", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
}
