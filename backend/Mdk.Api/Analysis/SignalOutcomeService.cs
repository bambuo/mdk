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
    SignalJournal journal,
    BinanceRestClient rest,
    IOptions<SignalOptions> signalOptions,
    ILogger<SignalOutcomeService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);

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
        var pending = journal.Snapshot()
            .Where(e => e.Outcome is null && now >= e.Time + MarketIntervals.IntervalSeconds(e.Interval) * _options.OutcomeHorizonBars)
            .GroupBy(e => (e.Market, e.Symbol, e.Interval))
            .ToList();

        foreach (var group in pending)
        {
            ct.ThrowIfCancellationRequested();
            var (marketStr, symbol, interval) = group.Key;
            if (!MarketKindExtensions.TryParse(marketStr, out var market)) continue;
            if (!TradingPair.TryParse(symbol, out var pair)) continue;

            Models.Candle[] candles;
            try
            {
                candles = await rest.GetKlinesAsync(market, pair, interval, 1000, ct);
            }
            catch (BinanceException ex)
            {
                logger.LogWarning("绩效评估拉取K线失败 {Symbol} {Interval}: {Message}", symbol, interval, ex.Message);
                continue;
            }
            if (candles.Length == 0) continue;

            var closes = candles.Select(c => c.Close).ToArray();
            var horizon = _options.OutcomeHorizonBars;

            foreach (var entry in group)
            {
                var idx = IndexOfTime(candles, entry.Time);
                if (idx < 0)
                {
                    // 信号过旧，超出可取K线范围
                    journal.MarkOutcome(entry.Key, new SignalOutcome { Status = "expired", EvaluatedAt = now });
                    continue;
                }
                if (idx + horizon >= candles.Length) continue; // 持有期未满，留待下轮

                var dir = entry.Side == "buy" ? 1 : -1;
                var exit = closes[idx + horizon];
                var ret = dir * (exit / entry.Price - 1);

                double driftSum = 0;
                long driftN = 0;
                for (var j = 0; j + horizon < closes.Length; j++)
                {
                    driftSum += closes[j + horizon] / closes[j] - 1;
                    driftN++;
                }
                var drift = driftN > 0 ? driftSum / driftN : 0;

                double mfe = double.MinValue;
                double mae = double.MaxValue;
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

                var feeRt = market == MarketKind.Futures ? 0.001 : 0.002;
                journal.MarkOutcome(entry.Key, new SignalOutcome
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
