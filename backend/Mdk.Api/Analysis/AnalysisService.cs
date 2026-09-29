using System.Collections.Concurrent;
using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Analysis;

/// <summary>组合「拉K线 + 纯函数分析」，供 REST 与 WS 共用；同时把新信号落库供事后绩效评估。</summary>
public sealed class AnalysisService(
    BinanceRestClient rest,
    IOptions<SignalOptions> signalOptions,
    IOptions<Chan.ChanOptions> chanOptions,
    SignalJournal journal)
{
    private static readonly TimeSpan HtfCacheTtl = TimeSpan.FromSeconds(60);

    private readonly SignalOptions _signalOptions = signalOptions.Value;
    private readonly Chan.ChanOptions _chanOptions = chanOptions.Value;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Models.Candle> Candles, DateTimeOffset At)> _htfCache = new();

    /// <summary>最近被请求过的组合（键 → (组合, 最近请求时间)），供关注列表定时分析使用。</summary>
    private readonly ConcurrentDictionary<string, (MarketKind Market, TradingPair Pair, string Interval, DateTimeOffset At)> _recent = new();

    /// <summary>返回最近关注的最多 max 个组合（按最近请求时间倒序）。</summary>
    public IReadOnlyList<(MarketKind Market, TradingPair Pair, string Interval)> RecentRequests(int max) =>
        _recent.Values
            .OrderByDescending(v => v.At)
            .Take(max)
            .Select(v => (v.Market, v.Pair, v.Interval))
            .ToList();

    public async Task<AnalysisResult> AnalyzeAsync(MarketKind market, TradingPair pair, string interval, int limit, CancellationToken ct = default)
    {
        _recent[$"{market}|{pair.Symbol}|{interval}"] = (market, pair, interval, DateTimeOffset.UtcNow);

        // 缠论使用固定内部窗口（最近 AnalysisBars 根），与显示窗口解耦——保证结构与信号可复现；
        // 因此取数上限需覆盖两者中较大者。
        var chanBars = _chanOptions.Enabled ? _chanOptions.AnalysisBars : 0;
        var fetchBars = Math.Clamp(Math.Max(limit, chanBars), 250, 1500);
        var fetched = await rest.GetKlinesAsync(market, pair, interval, fetchBars, ct);
        if (fetched.Length == 0)
            throw new BinanceException(-1, $"暂无K线数据：{pair.Display} {interval}");
        var (candles, chanWindow) = Chan.ChanWindowSelector.Select(fetched, limit, chanBars);

        // 多周期共振：取高一档周期的K线（缓存 60s），失败不阻塞主分析
        IReadOnlyList<Models.Candle>? htf = null;
        var htfInterval = MarketIntervals.HigherInterval(interval);
        if (htfInterval != null)
        {
            try
            {
                htf = await GetHtfCachedAsync(market, pair, htfInterval, ct);
            }
            catch (Exception ex) when (ex is BinanceException or HttpRequestException or TaskCanceledException)
            {
                // 高周期数据缺失时 TrendAligned 为 null，不产生共振标记
            }
        }

        // 次级别（下一档周期）数据：用于缠论买卖点的"次级别确认"；失败不阻塞主分析
        IReadOnlyList<Models.Candle>? subLevel = null;
        var subLevelInterval = MarketIntervals.LowerInterval(interval);
        if (_chanOptions.Enabled && _chanOptions.RequireSubLevelConfirm && subLevelInterval != null)
        {
            try
            {
                subLevel = await GetSubLevelCachedAsync(market, pair, subLevelInterval, ct);
            }
            catch (Exception ex) when (ex is BinanceException or HttpRequestException or TaskCanceledException)
            {
                // 次级别数据缺失时：不做次级别过滤（由 ChanOptions 语义决定，不阻塞）
            }
        }

        var result = AnalysisEngine.Compute(market, pair, interval, candles, _signalOptions, htf, htfInterval, _chanOptions, chanWindow, subLevel, subLevelInterval);
        RecordSignals(market, pair, interval, result);
        return result;
    }

    /// <summary>把本次分析产出的信号写入绩效日志（键去重：同一信号只记一次）。</summary>
    private void RecordSignals(MarketKind market, TradingPair pair, string interval, AnalysisResult result)
    {
        var marketKey = market.ToString().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var sig in result.Signals)
        {
            journal.TryRecord(new SignalJournalEntry
            {
                Key = $"{marketKey}|{pair.Symbol}|{interval}|{sig.Source}|{sig.Side}|{sig.Time}|{(sig.IsConfirmed ? 1 : 0)}",
                Market = marketKey,
                Symbol = pair.Symbol,
                Interval = interval,
                Source = sig.Source,
                Side = sig.Side,
                Time = sig.Time,
                Price = sig.Price,
                Note = sig.Note,
                StopPrice = sig.StopPrice,
                TrendAligned = sig.TrendAligned,
                Confluence = sig.Confluence,
                IsConfirmed = sig.IsConfirmed,
                Adx = sig.Adx,
                AtrPct = sig.AtrPct,
                BandwidthPct = sig.BandwidthPct,
                RecordedAt = now,
            });
        }
    }

    private async Task<IReadOnlyList<Models.Candle>> GetSubLevelCachedAsync(MarketKind market, TradingPair pair, string subInterval, CancellationToken ct)
    {
        var key = $"sub|{market}|{pair.Symbol}|{subInterval}";
        if (_htfCache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < HtfCacheTtl)
            return hit.Candles;

        // 次级别需要更多根才能形成完整笔结构（本级别 700 根 → 次级别约 700/4 根即可，取 300 留余量）
        var candles = await rest.GetKlinesAsync(market, pair, subInterval, 300, ct);
        _htfCache[key] = (candles, DateTimeOffset.UtcNow);
        return candles;
    }

    private async Task<IReadOnlyList<Models.Candle>> GetHtfCachedAsync(MarketKind market, TradingPair pair, string htfInterval, CancellationToken ct)
    {
        var key = $"{market}|{pair.Symbol}|{htfInterval}";
        if (_htfCache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < HtfCacheTtl)
            return hit.Candles;

        // 与缠论内部窗口同口径（回填与在线需一致），至少 260 根
        var bars = Math.Max(260, _chanOptions.AnalysisBars);
        var candles = await rest.GetKlinesAsync(market, pair, htfInterval, Math.Min(1000, bars), ct);
        _htfCache[key] = (candles, DateTimeOffset.UtcNow);
        return candles;
    }
}
