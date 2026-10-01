using System.Collections.Concurrent;
using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Analysis;

/// <summary>组合「拉K线 + 纯函数分析」，供 REST 与 WS 共用；同时把新信号落库供事后绩效评估。</summary>
public sealed class AnalysisService(
    BinanceRestClient rest,
    IOptions<Chan.ChanOptions> chanOptions,
    IOptions<PriceLevelOptions> levelOptions,
    VolumeProfileService profileService,
    SignalStore store)
{
    private static readonly TimeSpan HtfCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EvidenceCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>合约杠杆面事实的缓存时长（费率 8h 结算一次、持仓量 1h 粒度，60s 足够新鲜）。</summary>
    private static readonly TimeSpan LeverageCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, ((decimal? Funding, long? NextFundingTime, decimal? Mark, decimal? Basis, decimal? Oi, decimal? OiChange) Value, DateTimeOffset At)>
        _leverageCache = new();

    /// <summary>可信度表缓存（键 = 市场|周期）：台账只增不改，60s 内的统计不会影响使用判断。</summary>
    private readonly ConcurrentDictionary<string, (IReadOnlyList<EvidenceBucket> Buckets, DateTimeOffset At)>
        _evidenceCache = new();

    private readonly Chan.ChanOptions _chanOptions = chanOptions.Value;

    private readonly PriceLevelOptions _levelOptions = levelOptions.Value;

    private readonly ConcurrentDictionary<string, (IReadOnlyList<Models.Candle> Candles, DateTimeOffset At)> _htfCache =
        new();

    public async Task<AnalysisResult> AnalyzeAsync(
        MarketKind market, TradingPair pair, string interval, int limit, CancellationToken ct = default)
    {
        var chanOptions = _chanOptions;

        // 缠论与位点都用固定内部窗口（最近 N 根），与显示窗口解耦——保证结构与位点可复现；
        // 因此取数上限需覆盖三者中较大者。
        var chanBars = chanOptions.Enabled ? chanOptions.AnalysisBars : 0;
        var levelBars = _levelOptions.AnalysisBars;
        var fetchBars = Math.Clamp(Math.Max(Math.Max(limit, chanBars), levelBars), 250, 1500);
        var fetched = await rest.GetKlinesAsync(market, pair, interval, fetchBars, ct);
        if (fetched.Length == 0)
            throw new BinanceException(-1, $"暂无K线数据：{pair.Display} {interval}");
        var windows = AnalysisWindows.Select(fetched, limit, chanBars, levelBars);
        var candles = windows.Display;
        var chanWindow = windows.Chan;

        // 位点的价格基础：合约默认改用**标记价**K线（强平插针会在最新价K线上造出假极值，
        // 行业惯例用标记价衡量风险位；是否更优未检验）。拿不到时回落最新价，不阻塞分析。
        var levelCandles = windows.Levels;
        var levelsBasis = "last";
        if (market == MarketKind.Futures && _levelOptions.UseMarkPriceForFutures && levelBars > 0)
        {
            try
            {
                var mark = await rest.GetMarkPriceKlinesAsync(pair, interval, fetchBars, ct);
                if (mark.Length >= 100)
                {
                    levelCandles = AnalysisWindows.Tail(mark, levelBars);
                    levelsBasis = "mark";
                }
            }
            catch (Exception ex) when (ex is BinanceException or HttpRequestException or TaskCanceledException)
            {
                // 标记价不可用时用最新价K线计算位点
            }
        }

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
        if (chanOptions.Enabled && chanOptions.RequireSubLevelConfirm && subLevelInterval != null)
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

        // 多级别结构：次级别数据需覆盖"显示窗口 + 预热"，用于在图上叠加更细粒度的中枢
        IReadOnlyList<Models.Candle>? lowerLevel = null;
        var lowerLevelInterval = MarketIntervals.LowerInterval(interval);
        if (chanOptions.Enabled && chanOptions.MultiLevel && lowerLevelInterval != null)
        {
            try
            {
                lowerLevel = await GetLowerLevelCachedAsync(market, pair, lowerLevelInterval, candles[0].Time, ct);
            }
            catch (Exception ex) when (ex is BinanceException or HttpRequestException or TaskCanceledException)
            {
                // 次级别数据缺失时只返回本级别与高周期
            }
        }

        var result = AnalysisEngine.Compute(market, pair, interval, candles, htf, htfInterval,
            chanOptions, chanWindow, subLevel, subLevelInterval, lowerLevel, lowerLevelInterval,
            levelCandles, _levelOptions, levelsBasis, ProfileAnchors(market, pair));
        RecordSignals(market, pair, interval, result);
        var leverage = await BuildLeverageAsync(market, pair, candles, ct);
        return result with { Evidence = GetEvidenceCached(market, interval), Leverage = leverage };
    }

    /// <summary>
    /// 成交量分布锚点（POC / 价值区沿 / 高量与低量节点）：来自后台构建的缓存，未就绪返回 null
    /// （并已触发构建，数秒后的下一次分析即带上）。分箱均摊口径见 <see cref="VolumeProfile"/>。
    /// </summary>
    private IReadOnlyList<PriceAnchor>? ProfileAnchors(MarketKind market, TradingPair pair)
    {
        var profile = profileService.TryGet(market, pair);
        if (profile is null) return null;

        var anchors = new List<PriceAnchor>
        {
            new(profile.Poc, "成交量POC"),
            new(profile.VaHigh, "价值区上沿"),
            new(profile.VaLow, "价值区下沿"),
        };
        anchors.AddRange(profile.Hvn.Select(p => new PriceAnchor(p, "高量节点")));
        anchors.AddRange(profile.Lvn.Select(p => new PriceAnchor(p, "低量节点")));
        return anchors;
    }

    /// <summary>
    /// 杠杆面事实：主动买占比按当前显示窗口现算（便宜）；资金费率/持仓量走 60s 缓存（贵且变化慢）。
    /// 拿不到就是 null（现货本就没有费率与持仓量），不阻塞分析。
    /// </summary>
    private async Task<LeverageFacts> BuildLeverageAsync(
        MarketKind market, TradingPair pair, IReadOnlyList<Models.Candle> candles, CancellationToken ct)
    {
        var taker = LeverageMath.TakerBuyShare(candles);
        var bars = Math.Min(24, candles.Count);

        decimal? funding = null, mark = null, basis = null, openInterest = null, oiChange = null;
        long? nextFunding = null;
        if (market == MarketKind.Futures)
        {
            var cached = await GetFuturesLeverageCachedAsync(pair, ct);
            if (cached is { } value)
            {
                (funding, nextFunding, mark, basis, openInterest, oiChange) = value;
            }
        }

        return new LeverageFacts(funding, nextFunding, openInterest, oiChange, mark, basis, taker, bars);
    }

    /// <summary>合约侧事实（费率/标记价/持仓量）60s 缓存；失败不缓存，下次请求重试。</summary>
    private async Task<(decimal? Funding, long? NextFundingTime, decimal? Mark, decimal? Basis, decimal? Oi, decimal? OiChange)?>
        GetFuturesLeverageCachedAsync(TradingPair pair, CancellationToken ct)
    {
        if (_leverageCache.TryGetValue(pair.Symbol, out var hit) &&
            DateTimeOffset.UtcNow - hit.At < LeverageCacheTtl)
        {
            return hit.Value;
        }

        try
        {
            var premium = await rest.GetPremiumIndexAsync(pair, ct);
            var history = await rest.GetOpenInterestHistAsync(pair, "1h", 25, ct);
            var openInterest = history.Count > 0 ? history[^1].SumOpenInterest : (decimal?)null;
            var change = history.Count >= 2
                ? LeverageMath.ChangePct(history[0].SumOpenInterest, history[^1].SumOpenInterest)
                : null;
            var value = (
                Funding: premium.FundingRate,
                NextFundingTime: premium.NextFundingTime,
                Mark: premium.MarkPrice,
                Basis: LeverageMath.BasisPct(premium.MarkPrice, premium.IndexPrice),
                Oi: openInterest,
                OiChange: change);
            _leverageCache[pair.Symbol] = (value, DateTimeOffset.UtcNow);
            return value;
        }
        catch (Exception ex) when (ex is BinanceException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>把本次分析产出的信号写入台账（自然键去重：同一信号只记一条）。</summary>
    private void RecordSignals(MarketKind market, TradingPair pair, string interval, AnalysisResult result)
    {
        // 锚定币（USDC/USDT 等）价格恒定，其"信号"没有交易含义，不入台账以免污染绩效统计
        if (pair.IsPegged) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var sig in result.Signals)
        {
            store.TryRecord(new SignalEntry
            {
                Market = market,
                Pair = pair,
                Interval = interval,
                Source = sig.Source,
                Kind = sig.Kind,
                Side = sig.Side,
                Time = sig.Time,
                Price = sig.Price,
                Note = sig.Note,
                StopPrice = sig.StopPrice,
                ReferencePrice = sig.ReferencePrice,
                Confluence = sig.Confluence,
                IsConfirmed = sig.IsConfirmed,
                Adx = sig.Adx,
                AtrPct = sig.AtrPct,
                BandwidthPct = sig.BandwidthPct,
                JointScore = sig.JointScore?.Score,
                RecordedAt = now,
            });
        }
    }

    /// <summary>
    /// 可信度表（60s 缓存）：把台账里同市场同周期的样本按「来源 × 类别」分组，
    /// 给出经验胜率与 Wilson 区间、扣费后为正比例、中位超额与典型风险单位。
    /// 只做统计、不做筛选：样本不足的语境如实标记 <c>Sufficient=false</c>。
    /// </summary>
    private IReadOnlyList<EvidenceBucket> GetEvidenceCached(MarketKind market, string interval)
    {
        var key = $"{market}|{interval}";
        if (_evidenceCache.TryGetValue(key, out var cached)
            && DateTimeOffset.UtcNow - cached.At < EvidenceCacheTtl)
        {
            return cached.Buckets;
        }

        var pool = store.Snapshot()
            .Where(e => e.Market == market
                        && e.Interval == interval
                        && e.Source == "缠论" // 精简为纯缠论：历史台账里的 EMA/RSI/MACD/结构 样本不再进入可信度
                        && !e.Pair.IsPegged
                        && e.Outcome is { Status: "ok" })
            // 同一信号可能先记「盘中预警」再记「收盘确认」：按信号本体去重，优先确认版本
            .GroupBy(e => (e.Pair, e.Interval, e.Source, e.Kind, e.Side, e.Time))
            .Select(g => g.FirstOrDefault(e => e.IsConfirmed) ?? g.First())
            .ToList();

        var buckets = pool
            .GroupBy(e => e.Kind)
            .Select(g => EvidenceRules.Build(g.Key, interval, [.. g]))
            .OrderByDescending(b => b.NEpisodes)
            .ToList();

        _evidenceCache[key] = (buckets, DateTimeOffset.UtcNow);
        return buckets;
    }

    /// <summary>次级别K线（覆盖显示窗口 + 预热），用于多级别结构叠加。</summary>
    private async Task<IReadOnlyList<Models.Candle>> GetLowerLevelCachedAsync(
        MarketKind market, TradingPair pair, string lowerInterval, long displayFromTime, CancellationToken ct)
    {
        var key = $"lower|{market}|{pair.Symbol}|{lowerInterval}";
        var lowerSeconds = MarketIntervals.IntervalSeconds(lowerInterval);
        var warmup = _chanOptions.LowerLevelMinBars * lowerSeconds; // 次级别自身也需要预热才能形成结构
        var fromSec = displayFromTime - warmup;

        if (_htfCache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < HtfCacheTtl
                                                    && hit.Candles.Count > 0 && hit.Candles[0].Time <= fromSec)
        {
            return hit.Candles;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candles = await rest.GetKlinesRangeAsync(market, pair, lowerInterval, fromSec, now, ct);
        _htfCache[key] = (candles, DateTimeOffset.UtcNow);
        return candles;
    }

    private async Task<IReadOnlyList<Models.Candle>> GetSubLevelCachedAsync(MarketKind market, TradingPair pair,
        string subInterval, CancellationToken ct)
    {
        var key = $"sub|{market}|{pair.Symbol}|{subInterval}";
        if (_htfCache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < HtfCacheTtl)
            return hit.Candles;

        // 次级别需要更多根才能形成完整笔结构（本级别 700 根 → 次级别约 700/4 根即可，取 300 留余量）
        var candles = await rest.GetKlinesAsync(market, pair, subInterval, 300, ct);
        _htfCache[key] = (candles, DateTimeOffset.UtcNow);
        return candles;
    }

    private async Task<IReadOnlyList<Models.Candle>> GetHtfCachedAsync(MarketKind market, TradingPair pair,
        string htfInterval, CancellationToken ct)
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
