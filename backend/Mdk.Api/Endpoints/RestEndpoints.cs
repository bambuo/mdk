using Mdk.Api.Analysis;
using Mdk.Api.Analysis.Chan;
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
                                t.LastPrice, t.PriceChangePercent, t.QuoteVolume, pair.IsPegged);
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
            .WithSummary("缠论结构 + 买卖点信号 + 指标序列（纯缠论）");

        app.MapPost("/api/backfill/run", async (
                string? market, int? days, string? symbols, string? intervals, bool? subLevel,
                SignalBackfillService backfill, CancellationToken ct) =>
            {
                if (!MarketKindExtensions.TryParse(market, out var marketKind) || marketKind != MarketKind.Spot)
                    return BadRequest("历史回填目前仅支持现货（market=spot）");
                var symbolList = (symbols ?? "BTCUSDT,ETHUSDT,SOLUSDT,BNBUSDT,XRPUSDT")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var intervalList = (intervals ?? "1h,4h")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (symbolList.Length is 0 or > 30) return BadRequest("symbols 数量需在 1~30 之间");
                var badInterval = intervalList.FirstOrDefault(i => !MarketIntervals.IsValid(i));
                if (badInterval is not null) return BadRequest($"不支持的周期 “{badInterval}”");

                var status = backfill.Status;
                if (status.Running) return Results.Ok(status);   // 已有回填在跑，直接返回进度
                _ = backfill.RunAsync(Math.Clamp(days ?? 90, 7, 365), symbolList, intervalList, CancellationToken.None,
                    subLevel ?? true);
                return Results.Ok(backfill.Status);
            })
            .WithSummary("历史回填：拉取历史K线逐根复算缠论信号并入库（异步执行，用 status 查询进度）");

        app.MapGet("/api/backfill/status", (SignalBackfillService backfill) => Results.Ok(backfill.Status))
            .WithSummary("历史回填进度");

        app.MapGet("/api/signal-stats", (
                string? market, string? symbol, string? interval, int? days,
                SignalStore store) =>
            {
                if (!MarketKindExtensions.TryParse(market, out var marketKind))
                    return BadRequest($"不支持的市场 “{market}”，可用：spot, futures");
                var daysClamped = Math.Clamp(days ?? 90, 1, 365);
                var since = DateTimeOffset.UtcNow.AddDays(-daysClamped).ToUnixTimeSeconds();

                var pool = store.Snapshot()
                    .Where(e => e.Market == marketKind
                                && e.RecordedAt >= since
                                && e.Source == "缠论"           // 精简为纯缠论：只统计缠论买卖点
                                && !e.Pair.IsPegged            // 锚定币价格恒定，其样本没有交易含义
                                && e.Outcome is { Status: "ok" })
                    // 同一信号可能先以「盘中预警」、再以「收盘确认」各记一条：统计时按信号本体去重，
                    // 优先保留确认版本，避免样本量虚增（否则预警存活率与绩效都会被重复计数污染）
                    .GroupBy(e => (e.Market, e.Pair, e.Interval, e.Source, e.Side, e.Time))
                    .Select(g => g.FirstOrDefault(e => e.IsConfirmed) ?? g.First())
                    .ToList();
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    // 交易对统一按值对象比较（接受 BTCUSDT / BTC-USDT / BTC/USDT 三种写法）
                    var raw = symbol.Trim().ToUpperInvariant().Replace("/", "").Replace("-", "");
                    if (TradingPair.TryParse(raw, out var want)) pool = pool.Where(e => e.Pair == want).ToList();
                }
                if (!string.IsNullOrWhiteSpace(interval))
                {
                    var i = interval.Trim();
                    pool = pool.Where(e => e.Interval == i).ToList();
                }

                SignalSourceStats? Summarize(string source, IReadOnlyList<SignalEntry> items)
                {
                    if (items.Count == 0) return null;
                    var grade = SignalQualityRules.Evaluate(items);
                    // 与分级同底：比率一律按**独立波次**计算（原始条数只作为分母备注展示）
                    var episodes = SignalQualityRules.EpisodeRepresentatives(items);
                    return new SignalSourceStats(
                        Source: source,
                        N: items.Count,
                        NEpisodes: episodes.Count,
                        WinRate: Rate(episodes, e => e.Outcome!.Ret > 0),
                        AvgReturn: episodes.Average(e => e.Outcome!.Ret ?? 0),
                        AvgExcess: episodes.Average(e => e.Outcome!.Excess ?? 0),
                        NetPositiveRate: Rate(episodes, e => e.Outcome!.NetPositive == true),
                        StopHitRate: Rate(episodes, e => e.Outcome!.StopHit == true),
                        Grade: grade.Grade,
                        GradeReason: grade.Reason,
                        TopSymbolShare: grade.TopSymbolShare,
                        NBackfill: episodes.Count(e => e.Origin == "backfill"));
                }

                var bySource = pool
                    .GroupBy(e => e.Source)
                    .OrderByDescending(g => g.Count())
                    .Select(g => Summarize(g.Key, g.ToList())!)
                    .ToList();

                var windowEpisodes = SignalQualityRules.EpisodeRepresentatives(pool);
                return Results.Ok(new SignalStatsResponse(
                    WindowBasis: $"按记录时间近 {daysClamped} 天（回填样本的记录时间=运行时刻）",
                    WindowEpisodes: windowEpisodes.Count,
                    RealtimeEpisodes: windowEpisodes.Count(SignalQualityRules.IsRealtimeRecorded),
                    RealtimeRequired: SignalQualityRules.RealtimeMinEpisodes,
                    TotalEvaluated: pool.Count,
                    Overall: Summarize("全部", pool),
                    BySource: bySource,
                    ByRegime: BuildBuckets("状态", pool, e => RegimeLabel(e.Adx)),
                    ByConfluence: BuildBuckets("级别共振", pool.Where(e => e.Confluence != null).ToList(),
                        e => ConfluenceTagger.Label(e.Confluence))));
            })
            .WithSummary("信号历史绩效（分来源 + 分级 + 波次口径 + 分市场状态/共振状态）");
    }

    /// <summary>市场状态分桶：ADX 强度（趋势/过渡/震荡）。</summary>
    private static string RegimeLabel(decimal? adx) => adx switch
    {
        null => "早期样本(未记录状态)",
        >= 25 => "趋势市 ADX≥25",
        >= 20 => "过渡 20≤ADX<25",
        _ => "震荡市 ADX<20",
    };

    private static IReadOnlyList<SignalBucketStats> BuildBuckets(
        string _, IReadOnlyList<SignalEntry> pool, Func<SignalEntry, string> label)
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

    private static decimal Rate(IEnumerable<SignalEntry> items, Func<SignalEntry, bool> predicate) =>
        items.Count(predicate) / (decimal)items.Count();

    private static IResult BadRequest(string? message) =>
        Results.Json(new { error = message ?? "请求无效" }, statusCode: 400);

    private static IResult UpstreamError(BinanceException ex) =>
        Results.Json(new { error = ex.Message }, statusCode: 502);
}
