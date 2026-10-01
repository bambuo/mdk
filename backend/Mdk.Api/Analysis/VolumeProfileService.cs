using System.Collections.Concurrent;
using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Analysis;

/// <summary>
/// 成交量分布的构建与缓存（后台服务）。
///
/// 为什么不放在请求路径里：14 天 1 分钟K线约两万根（20 余次分页请求、数秒），
/// 放进分析请求会拖慢首屏。故：命中即用；未命中则**触发后台构建、本次先返回空**，
/// 数秒后的下一次推送就带上了；监控列表标的在启动后逐个预热。
///
/// 缓存只在内存（按 UTC 日失效）：重建成本可接受，不值得再开一个库。
/// 失败按天限次重试（默认 3 次），避免持续失败时被每个请求反复触发。
/// </summary>
public sealed class VolumeProfileService(
    BinanceRestClient rest,
    WatchlistStore watchlist,
    IOptions<PriceLevelOptions> options,
    ILogger<VolumeProfileService> logger) : BackgroundService
{
    private const int MaxAttemptsPerDay = 3;

    private readonly PriceLevelOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, VolumeProfileResult?> _results = new();
    private readonly ConcurrentDictionary<string, long> _builtDay = new();
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private readonly ConcurrentDictionary<string, byte> _building = new();

    /// <summary>读取（**不阻塞**）：未就绪时触发后台构建并返回 null。</summary>
    public VolumeProfileResult? TryGet(MarketKind market, TradingPair pair)
    {
        if (!_options.UseVolumeProfile || pair.IsEmpty) return null;
        var key = Key(market, pair);
        var day = Today();
        if (_builtDay.TryGetValue(key, out var built) && built == day) return _results.GetValueOrDefault(key);
        if (_attempts.GetValueOrDefault(key) >= MaxAttemptsPerDay) return null;   // 持续失败：当天不再重试
        if (_building.TryAdd(key, 1)) _ = BuildAsync(market, pair, key, day);
        return null;
    }

    /// <summary>预热：延迟一段时间后，为监控列表内启用的标的逐个构建（间隔 2s，避免打满权重）。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.ProfileWarmupDelaySeconds)), stoppingToken);
            foreach (var item in watchlist.All().Where(i => i.Enabled))
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (TryGet(item.Market, item.Pair) is null)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private async Task BuildAsync(MarketKind market, TradingPair pair, string key, long day)
    {
        try
        {
            _attempts[key] = _attempts.GetValueOrDefault(key) + 1;
            var toSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fromSec = toSec - (long)Math.Max(1, _options.ProfileDays) * 86_400;
            var candles = await rest.GetKlinesRangeAsync(market, pair, "1m", fromSec, toSec, CancellationToken.None);
            var profile = VolumeProfile.Compute(candles, Math.Max(10, _options.ProfileBins));
            _results[key] = profile;
            if (profile is not null)
            {
                _builtDay[key] = day;
                logger.LogInformation(
                    "成交量分布已构建 {Market} {Symbol}：{Bars} 根，POC {Poc}，价值区 {Low}~{High}",
                    market, pair.Symbol, profile.Bars, profile.Poc, profile.VaLow, profile.VaHigh);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "成交量分布构建失败 {Market} {Symbol}", market, pair.Symbol);
        }
        finally
        {
            _building.TryRemove(key, out _);
        }
    }

    private static long Today() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400;

    private static string Key(MarketKind market, TradingPair pair) => $"{market}|{pair.Symbol}";
}
