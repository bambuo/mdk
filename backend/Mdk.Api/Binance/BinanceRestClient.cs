using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mdk.Api.Analysis;
using Mdk.Api.Domain;
using Mdk.Api.Models;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Binance;

public sealed class BinanceOptions
{
    public const string SectionName = "Binance";

    /// <summary>现货 REST 基地址，被墙/受限时可换 data-api.binance.vision 等镜像。</summary>
    public string RestBaseUrl { get; set; } = "https://api.binance.com";

    /// <summary>现货 WebSocket 基地址，备用 data-stream.binance.vision。</summary>
    public string WsBaseUrl { get; set; } = "wss://stream.binance.com:9443";

    /// <summary>U 本位合约 REST 基地址。</summary>
    public string FuturesRestBaseUrl { get; set; } = "https://fapi.binance.com";

    /// <summary>U 本位合约 WebSocket 基地址。</summary>
    /// <summary>
    /// 合约 WebSocket 基址。**必须带数据分区路径**：币安已把合约流拆分为 /market 与 /public，
    /// 旧的无分区路径（wss://fstream.binance.com/ws/...）仍会接受握手但**不再推送任何数据帧**
    /// （2026-09-30 对照实验：旧路径 12s 收 0 帧；/market/ws 同条件收 33 帧）。
    /// </summary>
    public string FuturesWsBaseUrl { get; set; } = "wss://fstream.binance.com/market";
}

/// <summary>币安返回的业务错误（HTTP 状态可能仍是 200）。</summary>
public sealed class BinanceException(int code, string message)
    : Exception($"币安接口错误 {code}: {message}")
{
    public int Code { get; } = code;
}

public sealed class BinanceRestClient(
    HttpClient http,
    IOptions<BinanceOptions> options,
    ILogger<BinanceRestClient> logger)
{
    // http / options / logger 由主构造参数承载（捕获即为只读状态），不再声明同名字段
    private readonly Lock _sync = new();

    private readonly Dictionary<MarketKind, IReadOnlyList<Ticker24h>> _tickerCache = new();
    private readonly Dictionary<MarketKind, DateTimeOffset> _tickerCachedAt = new();
    private readonly Dictionary<MarketKind, IReadOnlyList<SymbolInfo>> _exchangeInfoCache = new();
    private readonly Dictionary<MarketKind, DateTimeOffset> _exchangeInfoCachedAt = new();


    private string RestBaseUrl(MarketKind market) =>
        market == MarketKind.Futures ? options.Value.FuturesRestBaseUrl : options.Value.RestBaseUrl;

    /// <summary>拉取K线（币安数组套数组格式 → 归一化 Candle，时间为秒级）。现货与合约响应结构一致。</summary>
    public async Task<Candle[]> GetKlinesAsync(MarketKind market, TradingPair pair, string interval, int limit,
        CancellationToken ct = default)
    {
        // 边界防线：空交易对（default(TradingPair)）不得发出请求——否则币安只会回一句
        // "Parameter 'symbol' was empty."，掩盖真正的问题（交易对解析失败）
        if (pair.IsEmpty)
            throw new ArgumentException($"交易对为空（{pair.Symbol}）：应先经交易对解析并校验 IsEmpty", nameof(pair));
        var url = $"{market.ToRestPath()}/klines?symbol={pair.Symbol}&interval={interval}&limit={limit}";
        using var response = await SendAsync(market, url, ct);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[][]>(cancellationToken: ct)
                   ?? throw new BinanceException(-1, "klines 返回为空");
        return ParseKlines(rows);
    }

    /// <summary>
    /// 标记价K线（**仅合约**，现货无此端点）：标记价由多所现货价与基差合成，
    /// 强平造成的插针不会进入该序列——用于计算支撑/阻力位点，避免最新价K线上的假极值。
    /// 响应结构与 klines 相同。
    /// </summary>
    public async Task<Candle[]> GetMarkPriceKlinesAsync(TradingPair pair, string interval, int limit,
        CancellationToken ct = default)
    {
        if (pair.IsEmpty)
            throw new ArgumentException($"交易对为空（{pair.Symbol}）：应先经交易对解析并校验 IsEmpty", nameof(pair));
        var url = $"{MarketKind.Futures.ToRestPath()}/markPriceKlines?symbol={pair.Symbol}&interval={interval}&limit={limit}";
        using var response = await SendAsync(MarketKind.Futures, url, ct);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[][]>(cancellationToken: ct)
                   ?? throw new BinanceException(-1, "markPriceKlines 返回为空");
        return ParseKlines(rows);
    }

    /// <summary>
    /// 按时间区间拉取K线（自动分页）：用于历史回填与"读取指定时间点的K线"。
    /// 从 fromSec 起（含）向 toSec 方向逐页拉取，每页上限 1000（合约 1500），页间短暂停顿以避免打满权重。
    /// </summary>
    public async Task<Candle[]> GetKlinesRangeAsync(
        MarketKind market, TradingPair pair, string interval, long fromSec, long toSec, CancellationToken ct = default)
    {
        var pageSize = market == MarketKind.Futures ? 1500 : 1000;
        var all = new List<Candle>();
        var cursor = fromSec * 1000;
        var endMs = toSec * 1000;
        var guard = 0;
        while (cursor <= endMs && guard++ < 200)
        {
            var url =
                $"{market.ToRestPath()}/klines?symbol={pair.Symbol}&interval={interval}&startTime={cursor}&endTime={endMs}&limit={pageSize}";
            using var response = await SendAsync(market, url, ct);
            var rows = await response.Content.ReadFromJsonAsync<JsonElement[][]>(cancellationToken: ct)
                       ?? throw new BinanceException(-1, "klines 返回为空");
            if (rows.Length == 0) break;
            var page = ParseKlines(rows);
            all.AddRange(page);
            var lastMs = rows[^1][0].GetInt64();
            if (page.Length < pageSize) break; // 已到区间末尾
            if (lastMs <= cursor) break; // 防死循环
            cursor = lastMs + 1;
            if (cursor <= endMs) await Task.Delay(120, ct); // 分页间让出速率
        }

        return [.. all];
    }

    /// <summary>
    /// 按时间区间拉取**标记价**K线（仅合约，自动分页）：研究用——线上合约位点就是按标记价算的，
    /// 检验必须用同一序列，否则"结论"与产品不同底。
    /// </summary>
    public async Task<Candle[]> GetMarkPriceKlinesRangeAsync(
        TradingPair pair, string interval, long fromSec, long toSec, CancellationToken ct = default)
    {
        if (pair.IsEmpty)
            throw new ArgumentException($"交易对为空（{pair.Symbol}）：应先经交易对解析并校验 IsEmpty", nameof(pair));
        var all = new List<Candle>();
        var cursor = fromSec * 1000;
        var endMs = toSec * 1000;
        var guard = 0;
        while (cursor <= endMs && guard++ < 400)
        {
            var url = $"{MarketKind.Futures.ToRestPath()}/markPriceKlines"
                + $"?symbol={pair.Symbol}&interval={interval}&startTime={cursor}&endTime={endMs}&limit=1500";
            using var response = await SendAsync(MarketKind.Futures, url, ct);
            var rows = await response.Content.ReadFromJsonAsync<JsonElement[][]>(cancellationToken: ct)
                       ?? throw new BinanceException(-1, "markPriceKlines 返回为空");
            if (rows.Length == 0) break;
            var page = ParseKlines(rows);
            all.AddRange(page);
            var lastMs = rows[^1][0].GetInt64();
            if (page.Length < 1500) break;
            if (lastMs <= cursor) break;
            cursor = lastMs + 1;
            if (cursor <= endMs) await Task.Delay(120, ct);
        }

        return [.. all];
    }

    private static Candle[] ParseKlines(JsonElement[][] rows)
    {
        var candles = new Candle[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            candles[i] = new Candle(
                Time: row[0].GetInt64() / 1000,
                Open: Num(row[1]),
                High: Num(row[2]),
                Low: Num(row[3]),
                Close: Num(row[4]),
                Volume: Num(row[5]),
                // 第 10 列：主动买成交量（现货与合约K线同结构；缺失时按 0 处理）
                TakerBuyVolume: row.Length > 9 ? Num(row[9]) : 0m);
        }

        return candles;
    }

    /// <summary>
    /// 合约资金费率与标记价（**仅合约**；现货无此端点）：标记价、指数价、当期资金费率、下次结算时间。
    /// 费率与基差是"当前位置是否拥挤"的事实，不构成方向判断。
    /// </summary>
    public async Task<PremiumIndex> GetPremiumIndexAsync(TradingPair pair, CancellationToken ct = default)
    {
        if (pair.IsEmpty)
            throw new ArgumentException($"交易对为空（{pair.Symbol}）：应先经交易对解析并校验 IsEmpty", nameof(pair));
        var url = $"{MarketKind.Futures.ToRestPath()}/premiumIndex?symbol={pair.Symbol}";
        using var response = await SendAsync(MarketKind.Futures, url, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new PremiumIndex(
            MarkPrice: Num(root.GetProperty("markPrice")),
            IndexPrice: Num(root.GetProperty("indexPrice")),
            FundingRate: root.TryGetProperty("lastFundingRate", out var rate) ? Num(rate) : null,
            NextFundingTime: root.TryGetProperty("nextFundingTime", out var next) ? next.GetInt64() / 1000 : null);
    }

    /// <summary>持仓量历史（`/futures/data/openInterestHist`，1h 粒度、最多 30 天）；用于算 24h 变化。</summary>
    public async Task<IReadOnlyList<OpenInterestPoint>> GetOpenInterestHistAsync(
        TradingPair pair, string period = "1h", int limit = 25, CancellationToken ct = default)
    {
        if (pair.IsEmpty)
            throw new ArgumentException($"交易对为空（{pair.Symbol}）：应先经交易对解析并校验 IsEmpty", nameof(pair));
        var url = $"/futures/data/openInterestHist?symbol={pair.Symbol}&period={period}&limit={limit}";
        using var response = await SendAsync(MarketKind.Futures, url, ct);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct) ?? [];
        return [.. rows.Select(row => new OpenInterestPoint(
            row.GetProperty("timestamp").GetInt64() / 1000,
            Num(row.GetProperty("sumOpenInterest")),
            Num(row.GetProperty("sumOpenInterestValue"))))];
    }

    /// <summary>全部交易对 24h 行情（每市场缓存 30 秒，避免多个请求打超频限制）。</summary>
    public async Task<IReadOnlyList<Ticker24h>> Get24hTickersAsync(MarketKind market, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_tickerCache.TryGetValue(market, out var cached) &&
                DateTimeOffset.UtcNow - _tickerCachedAt[market] < TimeSpan.FromSeconds(30))
                return cached;
        }

        using var response = await SendAsync(market, $"{market.ToRestPath()}/ticker/24hr", ct);
        var raw = await response.Content.ReadFromJsonAsync<List<TickerRaw>>(cancellationToken: ct) ?? [];
        var tickers = raw
            .Select(t => new Ticker24h(t.Symbol, D(t.LastPrice), D(t.PriceChangePercent), D(t.QuoteVolume)))
            .ToList();
        lock (_sync)
        {
            _tickerCache[market] = tickers;
            _tickerCachedAt[market] = DateTimeOffset.UtcNow;
        }

        return tickers;
    }

    /// <summary>交易所信息（含交易币/计价币拆分，每市场缓存 1 小时）。</summary>
    public async Task<IReadOnlyList<SymbolInfo>> GetExchangeInfoAsync(MarketKind market, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_exchangeInfoCache.TryGetValue(market, out var cached) &&
                DateTimeOffset.UtcNow - _exchangeInfoCachedAt[market] < TimeSpan.FromHours(1))
                return cached;
        }

        using var response = await SendAsync(market, $"{market.ToRestPath()}/exchangeInfo", ct);
        var raw = await response.Content.ReadFromJsonAsync<ExchangeInfoRaw>(cancellationToken: ct)
                  ?? throw new BinanceException(-1, "exchangeInfo 返回为空");
        var infos = new List<SymbolInfo>(raw.Symbols.Count);
        foreach (var s in raw.Symbols)
        {
            // 个别符号（如连写无法切分、或币种代码异常）直接跳过，不影响整体目录
            if (!TradingPair.TryParse(s.Symbol, out var parsedPair)) continue;
            try
            {
                infos.Add(new SymbolInfo(TradingPair.From(s.BaseAsset, s.QuoteAsset), s.Status));
            }
            catch (FormatException ex)
            {
                logger.LogWarning(ex, "跳过无法解析的交易对 {Symbol}", s.Symbol);
            }
        }

        lock (_sync)
        {
            _exchangeInfoCache[market] = infos;
            _exchangeInfoCachedAt[market] = DateTimeOffset.UtcNow;
        }

        return infos;
    }

    private async Task<HttpResponseMessage> SendAsync(MarketKind market, string path, CancellationToken ct)
    {
        var response = await http.GetAsync(RestBaseUrl(market).TrimEnd('/') + path, ct);
        if (response.IsSuccessStatusCode) return response;
        string message;
        try
        {
            message = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            message = response.ReasonPhrase ?? "请求失败";
        }

        logger.LogWarning("币安请求失败 {Market} {Status}: {Body}", market, (int)response.StatusCode, message);
        throw new BinanceException((int)response.StatusCode, message);
    }

    private static decimal Num(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    private sealed class TickerRaw
    {
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
        [JsonPropertyName("lastPrice")] public string LastPrice { get; set; } = "0";

        [JsonPropertyName("priceChangePercent")]
        public string PriceChangePercent { get; set; } = "0";

        [JsonPropertyName("quoteVolume")] public string QuoteVolume { get; set; } = "0";
    }

    private sealed class ExchangeInfoRaw
    {
        [JsonPropertyName("symbols")] public List<SymbolRaw> Symbols { get; set; } = [];
    }

    private sealed class SymbolRaw
    {
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
        [JsonPropertyName("baseAsset")] public string BaseAsset { get; set; } = "";
        [JsonPropertyName("quoteAsset")] public string QuoteAsset { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
    }
}

/// <summary>
/// 交易对目录：以币安 exchangeInfo（TRADING 状态）为准，
/// 提供「原始字符串 → 权威 TradingPair（交易币/计价币拆分）」的解析与校验。现货与合约各自维护一份。
/// </summary>
public sealed class SymbolCatalog(BinanceRestClient rest)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<MarketKind, Dictionary<string, TradingPair>> _bySymbol = new();

    /// <summary>指定市场的全部可交易对权威映射（symbol → TradingPair），首次调用时加载并缓存。</summary>
    public async Task<Dictionary<string, TradingPair>> GetAllAsync(MarketKind market, CancellationToken ct = default)
    {
        if (_bySymbol.TryGetValue(market, out var cached)) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (_bySymbol.TryGetValue(market, out var again)) return again;
            var infos = await rest.GetExchangeInfoAsync(market, ct);
            var map = infos
                .Where(s => s.Status == "TRADING")
                .GroupBy(s => s.Pair.Symbol, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Pair, StringComparer.Ordinal);
            _bySymbol[market] = map;
            return map;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>解析并校验交易对；返回以币安 exchangeInfo 为准的权威拆分，该市场下未知交易对返回 null。</summary>
    public async Task<TradingPair?> ResolveAsync(MarketKind market, string raw, CancellationToken ct = default)
    {
        var all = await GetAllAsync(market, ct);
        return Lookup(all, raw);
    }

    /// <summary>
    /// 目录查找（纯函数，可测）：未命中返回 **null**。
    /// 不用 <c>GetValueOrDefault</c>——TradingPair 是结构体，未命中会得到非 null 的空结构，
    /// 从而绕过调用方的 null 检查、把空 symbol 发给币安（2026-10-01 实际事故）。
    /// </summary>
    public static TradingPair? Lookup(Dictionary<string, TradingPair> bySymbol, string raw)
    {
        var canonical = raw.Trim().ToUpperInvariant();
        return bySymbol.TryGetValue(canonical, out var pair) ? pair : null;
    }
}
