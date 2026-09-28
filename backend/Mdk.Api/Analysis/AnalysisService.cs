using Mdk.Api.Binance;
using Mdk.Api.Domain;

namespace Mdk.Api.Analysis;

/// <summary>组合「拉K线 + 纯函数分析」，供 REST 与 WS 共用。</summary>
public sealed class AnalysisService(BinanceRestClient rest)
{
    public async Task<AnalysisResult> AnalyzeAsync(MarketKind market, TradingPair pair, string interval, int limit, CancellationToken ct = default)
    {
        var candles = await rest.GetKlinesAsync(market, pair, interval, limit, ct);
        if (candles.Length == 0)
            throw new BinanceException(-1, $"暂无K线数据：{pair.Display} {interval}");
        return AnalysisEngine.Compute(market, pair, interval, candles);
    }
}
