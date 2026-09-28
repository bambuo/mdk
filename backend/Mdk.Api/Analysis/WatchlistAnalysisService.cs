using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Analysis;

/// <summary>
/// 关注列表定时分析：把最近被请求过的 (市场, 币种, 周期) 组合定期重新分析一遍。
/// 目的：无浏览器连接时也持续积累信号与「盘中预警」样本（预警只在盘中计算时才产生），
/// 让绩效统计与预警存活率有足够样本，而不是依赖用户恰好开着页面。
/// </summary>
public sealed class WatchlistAnalysisService(
    AnalysisService analysisService,
    IOptions<SignalOptions> signalOptions,
    ILogger<WatchlistAnalysisService> logger) : BackgroundService
{
    private readonly SignalOptions _options = signalOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WatchlistEnabled)
        {
            logger.LogInformation("关注列表定时分析已禁用（Signal:WatchlistEnabled=false）");
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.WatchlistIntervalSeconds));
        logger.LogInformation("关注列表定时分析已启动（每 {Seconds}s 分析最近关注的最多 {Max} 个组合）",
            interval.TotalSeconds, _options.WatchlistMaxTriples);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var (market, pair, itv) in analysisService.RecentRequests(_options.WatchlistMaxTriples))
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    await analysisService.AnalyzeAsync(market, pair, itv, 500, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "关注列表分析失败 {Symbol} {Interval}", pair.Display, itv);
                }
            }
        }
    }
}
