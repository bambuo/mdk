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
    }

    private static IResult BadRequest(string? message) =>
        Results.Json(new { error = message ?? "请求无效" }, statusCode: 400);

    private static IResult UpstreamError(BinanceException ex) =>
        Results.Json(new { error = ex.Message }, statusCode: 502);
}
