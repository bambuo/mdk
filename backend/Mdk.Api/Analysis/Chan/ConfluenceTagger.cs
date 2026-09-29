namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 级别共振标签：判断本级别（L0）买卖点在"前 N 根高周期（L1）K线"内是否有高周期的同向/反向缠论买卖点。
///
/// 机制依据（2026-09-29 回填样本探测，90 天 1877 条）：1h 信号前 1 根 4h 内出现同向 4h 缠论信号时，
/// 胜率 70.7m%、扣费后为正 71%（无共振组 45.5m% / 38%）；而"高周期均线对齐"与 ADX 无区分度。
/// 窗口越紧越好的形态与该机制解释一致（两个级别在同一时刻完成同向结构转折）。
///
/// 严格无未来函数：只使用"记账时间 ≤ 本级别信号时间"的高周期信号；
/// 高周期信号由 ChanBackfill/ChanAnalyzer 产出，其记账时间即"该信号可知"的时刻。
/// </summary>
public static class ConfluenceTagger
{
    public const string Aligned = "aligned";   // 窗口内有同向高周期信号
    public const string Counter = "counter";   // 窗口内只有反向高周期信号
    public const string None = "none";         // 窗口内无高周期信号

    /// <summary>为一批 L0 信号打标签。windowSeconds = 窗口根数 × 高周期秒数。</summary>
    public static Dictionary<(long Time, string Side), string> Tag(
        IReadOnlyList<ChanBuySellPoint> htfPoints,
        long windowSeconds,
        IEnumerable<(long Time, string Side)> signals)
    {
        var tags = new Dictionary<(long, string), string>();
        foreach (var (time, side) in signals)
        {
            var from = time - windowSeconds;
            var aligned = false;
            var counter = false;
            foreach (var point in htfPoints)
            {
                if (point.Time < from || point.Time > time) continue;
                if (point.Side == side) aligned = true;
                else counter = true;
                if (aligned) break;   // 同向优先，无需继续扫
            }
            tags[(time, side)] = aligned ? Aligned : counter ? Counter : None;
        }
        return tags;
    }

    /// <summary>标签的中文展示（前端与统计用）。</summary>
    public static string Label(string? tag) => tag switch
    {
        Aligned => "共振",
        Counter => "逆向共振",
        _ => "无共振",
    };
}
