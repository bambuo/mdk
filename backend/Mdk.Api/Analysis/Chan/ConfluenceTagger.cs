namespace Mdk.Api.Analysis.Chan;

/// <summary>
/// 级别共振标签：判断本级别（L0）买卖点在"前 N 根高周期（L1）K线"内是否有高周期的同向/反向缠论买卖点。
///
/// **证据状态（2026-09-30 更新，务必先读）**：标签本身只做结构化描述（同向/反向/无），
/// 但**"共振组表现更好"这一说法已被本项目自己否证**：早期 19 波次探测曾显示胜率 70.7%，
/// 扩样后判据未通过——级别共振无统计上可辨的优势（PLAN §0.11 记为负结果；band 用样本
/// 检验时 t≈0.53）。因此标签**只能用于标注，不得据此过滤或提升"可信度"**；
/// 若未来要用它做筛选，必须重新预注册并走样本外检验。
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
