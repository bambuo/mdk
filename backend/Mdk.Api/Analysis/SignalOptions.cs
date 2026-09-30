namespace Mdk.Api.Analysis;

/// <summary>
/// 信号参数（appsettings "Signal" 节）。
/// 2026-09-30 精简为纯缠论：EMA/RSI/MACD/结构 信号源已删除，仅保留事后评估持有期与关注列表参数。
/// </summary>
public sealed class SignalOptions
{
    public const string SectionName = "Signal";

    public static SignalOptions Default { get; } = new();

    /// <summary>事后绩效评估的持有期（根数）。</summary>
    public int OutcomeHorizonBars { get; set; } = 12;

    /// <summary>是否对关注列表定时分析（无浏览器连接时也持续积累信号样本）。</summary>
    public bool WatchlistEnabled { get; set; } = true;

    /// <summary>监控列表分析间隔（秒）。</summary>
    public int WatchlistIntervalSeconds { get; set; } = 120;
}
