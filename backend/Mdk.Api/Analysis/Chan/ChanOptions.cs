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
    public double DivergenceAreaRatio { get; set; } = 0.9;

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

    public int MinMergedBarsBetween => StrokeMode.Equals("Strict", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
}
