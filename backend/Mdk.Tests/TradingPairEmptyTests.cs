using Mdk.Api.Binance;
using Mdk.Api.Domain;

namespace Mdk.Tests;

/// <summary>
/// 结构体值对象的"空值陷阱"回归：TradingPair 是 struct，字典未命中会返回非 null 的空结构，
/// 只判 null 会让空交易对一路传到币安（2026-10-01 实际事故：symbol' was empty）。
/// 本组用例锁定三处防线：值对象可识别空值、目录查找未命中返回 null、空交易对不得发出请求。
/// </summary>
public static class TradingPairEmptyTests
{
    public static void Register(TestKit t)
    {
        t.Case("空值陷阱_default结构可被识别为空", () =>
        {
            TradingPair empty = default;
            Assert.Equal(true, empty.IsEmpty);
            Assert.Equal("", empty.Symbol);
            Assert.Equal(false, TradingPair.Parse("BTCUSDT").IsEmpty);
        });

        t.Case("空值陷阱_目录查找未命中返回null而不是空结构", () =>
        {
            var bySymbol = new Dictionary<string, TradingPair>(StringComparer.Ordinal)
            {
                ["BTCUSDT"] = TradingPair.Parse("BTCUSDT"),
            };
            var hit = SymbolCatalog.Lookup(bySymbol, "btcusdt");       // 大小写与空白归一
            Assert.Equal(false, hit is null);
            Assert.Equal("BTC", hit!.Value.BaseAsset);

            var miss = SymbolCatalog.Lookup(bySymbol, "FOOUSDT");
            Assert.Equal(true, miss is null);                              // 关键：是 null，不是 default
            Assert.Equal(true, SymbolCatalog.Lookup(bySymbol, "  ") is null);
        });

        t.Case("空值陷阱_空交易对不得发出K线请求", () =>
        {
            var http = new HttpClient();
            var options = Microsoft.Extensions.Options.Options.Create(new BinanceOptions());
            var client = new BinanceRestClient(http, options,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BinanceRestClient>.Instance);
            var threw = false;
            try
            {
                _ = client.GetKlinesAsync(MarketKind.Spot, default, "1h", 10).GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                threw = true;   // 本地清晰报错，而不是把空 symbol 发给币安换回 400
            }
            Assert.Equal(true, threw);
        });
    }
}
