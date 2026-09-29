using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Analysis;

/// <summary>
/// 信号事后绩效服务：定期扫描「持有期已满、尚未评估」的落库信号，
/// 拉取其后的K线，回填收益/最大浮盈浮亏/止损命中/剔除漂移的超额/扣费后正负。
/// 这是把「置信度」从启发式分数变成可验证统计量的基础。
/// </summary>
public sealed class SignalOutcomeService(
    SignalStore store,
    BinanceRestClient rest,
    IOptions<SignalOptions> signalOptions,
    ILogger<SignalOutcomeService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);

    /// <summary>单周期每轮最多评估的待回填条数（避免一次拉取过大区间）。</summary>
    private const int MaxPerInterval = 500;

    private readonly SignalOptions _options = signalOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("信号绩效评估服务已启动（每 {Seconds}s 扫描一次，持有期 {Bars} 根）",
            ScanInterval.TotalSeconds, _options.OutcomeHorizonBars);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "信号绩效扫描异常");
            }
            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task ScanOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 待评估集合由台账按"各周期持有期已满"筛选（SQL 侧完成），每周期最多取 MaxPerInterval 条
        var pending = store.PendingOutcomes(now, _options.OutcomeHorizonBars, MaxPerInterval)
            .GroupBy(e => (e.Market, e.Pair, e.Interval))
            .ToList();

        foreach (var group in pending)
        {
            ct.ThrowIfCancellationRequested();
            var (market, pair, interval) = group.Key;

            // 按该组待评估信号的**时间范围**取K线（而非固定取最近 1000 根）：
            // 历史回填样本可能早于最近 1000 根，用固定窗口会被误判为 expired。
            var barSeconds = MarketIntervals.IntervalSeconds(interval);
            var horizonBars = _options.OutcomeHorizonBars;
            var fromSec = group.Min(e => e.Time) - barSeconds;
            var toSec = group.Max(e => e.Time) + barSeconds * (horizonBars + 2);
            Models.Candle[] candles;
            try
            {
                candles = await rest.GetKlinesRangeAsync(market, pair, interval, fromSec, toSec, ct);
            }
            catch (BinanceException ex)
            {
                logger.LogWarning("绩效评估拉取K线失败 {Symbol} {Interval}: {Message}", pair.Symbol, interval, ex.Message);
                continue;
            }
            if (candles.Length == 0) continue;

            var closes = candles.Select(c => c.Close).ToArray();
            var horizon = horizonBars;

            foreach (var entry in group)
            {
                var idx = IndexOfTime(candles, entry.Time);
                if (idx < 0)
                {
                    // 信号过旧，超出可取K线范围
                    store.MarkOutcome(entry.Id, new SignalOutcome { Status = "expired", EvaluatedAt = now });
                    continue;
                }
                if (idx + horizon >= candles.Length) continue; // 持有期未满，留待下轮

                var dir = entry.Side == "buy" ? 1 : -1;
                var exit = closes[idx + horizon];
                var ret = dir * (exit / entry.Price - 1);

                decimal driftSum = 0;
                long driftN = 0;
                for (var j = 0; j + horizon < closes.Length; j++)
                {
                    driftSum += closes[j + horizon] / closes[j] - 1;
                    driftN++;
                }
                var drift = driftN > 0 ? driftSum / driftN : 0;

                decimal mfe = decimal.MinValue;
                decimal mae = decimal.MaxValue;
                var stopHit = false;
                for (var k = idx; k <= idx + horizon && k < candles.Length; k++)
                {
                    var r1 = dir * (candles[k].High / entry.Price - 1);
                    var r2 = dir * (candles[k].Low / entry.Price - 1);
                    mfe = Math.Max(mfe, Math.Max(r1, r2));
                    mae = Math.Min(mae, Math.Min(r1, r2));
                    if (entry.StopPrice is { } stop && (dir == 1 ? candles[k].Low <= stop : candles[k].High >= stop))
                        stopHit = true;
                }

                var feeRt = market == MarketKind.Futures ? 0.001m : 0.002m;
                store.MarkOutcome(entry.Id, new SignalOutcome
                {
                    Status = "ok",
                    Ret = ret,
                    Excess = ret - dir * drift,
                    Mfe = mfe,
                    Mae = mae,
                    StopHit = stopHit,
                    NetPositive = ret - feeRt > 0,
                    EvaluatedAt = now,
                });
            }
        }
    }

    private static int IndexOfTime(Models.Candle[] candles, long time)
    {
        for (var i = 0; i < candles.Length; i++)
            if (candles[i].Time == time)
                return i;
        return -1;
    }
}
