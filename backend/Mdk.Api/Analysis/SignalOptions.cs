namespace Mdk.Api.Analysis;

/// <summary>
/// 信号引擎参数（appsettings "Signal" 节）。
/// EMA 提速依据：2026-09-28 实测（3 币 × 现货 1h/4h × 1000 根），1h 周期 EMA(10,30) 交叉
/// 相比 EMA(20,50) 更及时（信号前已走 0.81% vs 1.71%）且更准（胜率 58.3% vs 54.4%，t=2.21），
/// 但 4h 周期提速为负贡献，故仅对 1h 启用。持续以 /api/signal-stats 的事后绩效复核。
/// </summary>
public sealed class SignalOptions
{
    public const string SectionName = "Signal";

    public static SignalOptions Default { get; } = new();

    /// <summary>1h 周期用快线参数（10/30）生成信号；显示用的 EMA20/50/200 不受影响。</summary>
    public bool UseFastEmaOn1h { get; set; } = true;

    public int FastEmaPeriod { get; set; } = 10;

    public int FastEmaSlowPeriod { get; set; } = 30;

    /// <summary>同源同向信号的冷却期（根数）：期内重复触发不重复报，抑制趋势中段的无效交叉。</summary>
    public int CooldownBars { get; set; } = 6;

    /// <summary>事后绩效评估的持有期（根数）。</summary>
    public int OutcomeHorizonBars { get; set; } = 12;

    /// <summary>是否启用「结构确认」实验信号源（触及已确认支撑/阻力并收回）。</summary>
    public bool IncludeStructureSignals { get; set; } = true;

    /// <summary>是否对关注列表定时分析（无浏览器连接时也持续积累信号与盘中预警样本）。</summary>
    public bool WatchlistEnabled { get; set; } = true;

    /// <summary>关注列表最多跟踪的 (市场,币种,周期) 组合数。</summary>
    public int WatchlistMaxTriples { get; set; } = 10;

    /// <summary>关注列表分析间隔（秒）。</summary>
    public int WatchlistIntervalSeconds { get; set; } = 120;
}
