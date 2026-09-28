using Mdk.Api.Analysis;
using Mdk.Api.Binance;
using Mdk.Api.Domain;

namespace Mdk.Api.Endpoints;

/// <summary>把原始 market + symbol 字符串转成值对象的唯一入口：本地快速解析 + exchangeInfo 权威校准。</summary>
internal static class PairResolver
{
    public static async Task<(MarketKind Market, TradingPair? Pair, string? Error)> ResolveAsync(
        string? raw, string? market, SymbolCatalog catalog, CancellationToken ct)
    {
        if (!MarketKindExtensions.TryParse(market, out var marketKind))
            return (MarketKind.Spot, null, $"不支持的市场 “{market}”，可用：spot, futures");
        if (string.IsNullOrWhiteSpace(raw))
            return (marketKind, null, "缺少 symbol 参数（格式如 BTCUSDT）");
        if (!TradingPair.TryParse(raw, out var parsed))
            return (marketKind, null, $"无法识别的交易对 “{raw}”（格式如 BTCUSDT）");
        var canonical = await catalog.ResolveAsync(marketKind, parsed.Symbol, ct);
        return canonical is null
            ? (marketKind, null, $"币安{MarketName(marketKind)}不存在可交易的交易对 {parsed.Display}")
            : (marketKind, canonical, null);
    }

    private static string MarketName(MarketKind market) =>
        market == MarketKind.Futures ? "U本位合约" : "现货";
}

internal static class RestEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/symbols", async (
                string? market, string? quote, int? limit,
                SymbolCatalog catalog, BinanceRestClient rest, CancellationToken ct) =>
            {
                if (!MarketKindExtensions.TryParse(market, out var marketKind))
                    return BadRequest($"不支持的市场 “{market}”，可用：spot, futures");
                var quoteAsset = (quote ?? "USDT").Trim().ToUpperInvariant();
                if (quoteAsset.Length is < 2 or > 20 || !quoteAsset.All(char.IsAsciiLetterOrDigit))
                    return BadRequest("quote 参数无效（应为币种代码，如 USDT）");
                var take = Math.Clamp(limit ?? 50, 1, 200);
                try
                {
                    var pairs = await catalog.GetAllAsync(marketKind, ct);
                    var tickers = await rest.Get24hTickersAsync(marketKind, ct);
                    var rows = tickers
                        .Where(t => pairs.TryGetValue(t.Symbol, out var pair) && pair.QuoteAsset == quoteAsset)
                        .OrderByDescending(t => t.QuoteVolume)
                        .Take(take)
                        .Select(t =>
                        {
                            var pair = pairs[t.Symbol];
                            return new SymbolQuote(
                                t.Symbol, pair.BaseAsset, pair.QuoteAsset,
                                t.LastPrice, t.PriceChangePercent, t.QuoteVolume);
                        })
                        .ToList();
                    return Results.Ok(rows);
                }
                catch (BinanceException ex)
                {
                    return UpstreamError(ex);
                }
            })
            .WithSummary("交易对列表（按 24h 成交额排序，含交易币/计价币拆分；market=spot|futures）");

        app.MapGet("/api/klines", async (
                string? market, string? symbol, string? interval, int? limit,
                SymbolCatalog catalog, BinanceRestClient rest, CancellationToken ct) =>
            {
                var (marketKind, pair, error) = await PairResolver.ResolveAsync(symbol, market, catalog, ct);
                if (pair is null) return BadRequest(error);
                if (interval is null || !MarketIntervals.IsValid(interval))
                    return BadRequest($"不支持的周期 “{interval}”，可用：{string.Join(", ", MarketIntervals.All)}");
                var take = Math.Clamp(limit ?? 500, 50, 1000);
                try
                {
                    var candles = await rest.GetKlinesAsync(marketKind, pair.Value, interval, take, ct);
                    return Results.Ok(new KlinesResponse(
                        marketKind.ToString().ToLowerInvariant(),
                        pair.Value.Symbol, pair.Value.BaseAsset, pair.Value.QuoteAsset, interval, candles));
                }
                catch (BinanceException ex)
                {
                    return UpstreamError(ex);
                }
            })
            .WithSummary("K线快照（秒级时间戳 + OHLCV）");

        app.MapGet("/api/analysis", async (
                string? market, string? symbol, string? interval, int? limit,
                SymbolCatalog catalog, AnalysisService analysisService, CancellationToken ct) =>
            {
                var (marketKind, pair, error) = await PairResolver.ResolveAsync(symbol, market, catalog, ct);
                if (pair is null) return BadRequest(error);
                if (interval is null || !MarketIntervals.IsValid(interval))
                    return BadRequest($"不支持的周期 “{interval}”，可用：{string.Join(", ", MarketIntervals.All)}");
                var take = Math.Clamp(limit ?? 500, 250, 1000);
                try
                {
                    var result = await analysisService.AnalyzeAsync(marketKind, pair.Value, interval, take, ct);
                    return Results.Ok(result);
                }
                catch (BinanceException ex)
                {
                    return UpstreamError(ex);
                }
            })
            .WithSummary("趋势方向 + 关键点位 + 买卖点信号 + 指标序列");

        app.MapGet("/api/signal-stats", (
                string? market, string? symbol, string? interval, int? days,
                SignalJournal journal) =>
            {
                if (!MarketKindExtensions.TryParse(market, out var marketKind))
                    return BadRequest($"不支持的市场 “{market}”，可用：spot, futures");
                var marketKey = marketKind.ToString().ToLowerInvariant();
                var daysClamped = Math.Clamp(days ?? 90, 1, 365);
                var since = DateTimeOffset.UtcNow.AddDays(-daysClamped).ToUnixTimeSeconds();

                var pool = journal.Snapshot()
                    .Where(e => e.Market == marketKey
                                && e.RecordedAt >= since
                                && e.Outcome is { Status: "ok" })
                    // 同一信号可能先以「盘中预警」、再以「收盘确认」各记一条：统计时按信号本体去重，
                    // 优先保留确认版本，避免样本量虚增（否则预警存活率与绩效都会被重复计数污染）
                    .GroupBy(e => (e.Market, e.Symbol, e.Interval, e.Source, e.Side, e.Time))
                    .Select(g => g.FirstOrDefault(e => e.IsConfirmed) ?? g.First())
                    .ToList();
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    var s = symbol.Trim().ToUpperInvariant();
                    pool = pool.Where(e => e.Symbol == s).ToList();
                }
                if (!string.IsNullOrWhiteSpace(interval))
                {
                    var i = interval.Trim();
                    pool = pool.Where(e => e.Interval == i).ToList();
                }

                SignalSourceStats? Summarize(string source, IReadOnlyList<SignalJournalEntry> items)
                {
                    if (items.Count == 0) return null;
                    return new SignalSourceStats(
                        Source: source,
                        N: items.Count,
                        NEpisodes: CountEpisodes(items),
                        WinRate: Rate(items, e => e.Outcome!.Ret > 0),
                        AvgReturn: items.Average(e => e.Outcome!.Ret ?? 0),
                        AvgExcess: items.Average(e => e.Outcome!.Excess ?? 0),
                        NetPositiveRate: Rate(items, e => e.Outcome!.NetPositive == true),
                        StopHitRate: Rate(items, e => e.Outcome!.StopHit == true),
                        Grade: Grade(items));
                }

                var bySource = pool
                    .GroupBy(e => e.Source)
                    .OrderByDescending(g => g.Count())
                    .Select(g => Summarize(g.Key, g.ToList())!)
                    .ToList();

                return Results.Ok(new SignalStatsResponse(
                    TotalEvaluated: pool.Count,
                    Overall: Summarize("全部", pool),
                    BySource: bySource,
                    ByRegime: BuildBuckets("状态", pool, e => RegimeLabel(e.Adx)),
                    ByAlignment: BuildBuckets("共振", pool, e => AlignmentLabel(e.TrendAligned))));
            })
            .WithSummary("信号历史绩效（分来源 + 分级 + 波次口径 + 分市场状态/共振状态）");
    }

    /// <summary>市场状态分桶：ADX 强度（趋势/过渡/震荡）。</summary>
    private static string RegimeLabel(double? adx) => adx switch
    {
        null => "早期样本(未记录状态)",
        >= 25 => "趋势市 ADX≥25",
        >= 20 => "过渡 20≤ADX<25",
        _ => "震荡市 ADX<20",
    };

    private static string AlignmentLabel(bool? aligned) => aligned switch
    {
        true => "顺大势",
        false => "逆大势",
        null => "无高周期数据",
    };

    private static IReadOnlyList<SignalBucketStats> BuildBuckets(
        string _, IReadOnlyList<SignalJournalEntry> pool, Func<SignalJournalEntry, string> label)
    {
        return pool
            .GroupBy(label)
            .OrderByDescending(g => g.Count())
            .Select(g => new SignalBucketStats(
                Label: g.Key,
                N: g.Count(),
                WinRate: Rate(g, e => e.Outcome!.Ret > 0),
                AvgExcess: g.Average(e => e.Outcome!.Excess ?? 0),
                NetPositiveRate: Rate(g, e => e.Outcome!.NetPositive == true)))
            .ToList();
    }

    /// <summary>
    /// 波次口径：同币种/周期/方向、间隔 24 根以内的信号视为同一波行情（事件聚类），
    /// 用于修正"信号高度时间聚集导致 t 值虚高"的问题（一个波段算一个有效样本）。
    /// </summary>
    private static int CountEpisodes(IReadOnlyList<SignalJournalEntry> items)
    {
        var episodes = 0;
        foreach (var group in items.GroupBy(e => (e.Symbol, e.Interval, e.Side)))
        {
            var times = group.Select(e => e.Time).OrderBy(t => t).ToList();
            var barSeconds = MarketIntervals.IntervalSeconds(group.Key.Interval);
            var previous = long.MinValue;
            foreach (var t in times)
            {
                if (previous == long.MinValue || t - previous > barSeconds * 24)
                    episodes++;
                previous = t;
            }
        }
        return episodes;
    }

    /// <summary>绩效分级：按样本量 + 扣费后为正比例给出可直接使用的准入标签。</summary>
    private static string Grade(IReadOnlyList<SignalJournalEntry> items)
    {
        var n = items.Count;
        if (n < 30) return "样本不足";
        var netPositive = Rate(items, e => e.Outcome!.NetPositive == true);
        var avgExcess = items.Average(e => e.Outcome!.Excess ?? 0);
        if (netPositive >= 0.5 && avgExcess > 0) return "可参考";
        if (netPositive >= 0.35) return "仅观察";
        return "不达标";
    }

    private static double Rate(IEnumerable<SignalJournalEntry> items, Func<SignalJournalEntry, bool> predicate) =>
        items.Count(predicate) / (double)items.Count();

    private static IResult BadRequest(string? message) =>
        Results.Json(new { error = message ?? "请求无效" }, statusCode: 400);

    private static IResult UpstreamError(BinanceException ex) =>
        Results.Json(new { error = ex.Message }, statusCode: 502);
}
