using System.Text.Json;
using Mdk.Api.Domain;

namespace Mdk.Tests;

/// <summary>交易对值对象：连写解析、交易币/计价币拆分、值相等、JSON 往返。</summary>
public static class TradingPairTests
{
    public static void Register(TestKit t)
    {
        t.Case("Parse_BTCUSDT_拆分为BTC与USDT", () =>
        {
            var pair = TradingPair.Parse("BTCUSDT");
            Assert.Equal("BTC", pair.BaseAsset);
            Assert.Equal("USDT", pair.QuoteAsset);
            Assert.Equal("BTCUSDT", pair.Symbol);
            Assert.Equal("BTC/USDT", pair.Display);
        });

        t.Case("Parse_大小写不敏感与首尾空白", () =>
        {
            var pair = TradingPair.Parse(" ethbtc ");
            Assert.Equal("ETH", pair.BaseAsset);
            Assert.Equal("BTC", pair.QuoteAsset);
        });

        t.Case("Parse_长计价币后缀优先_FDUSD与USDC", () =>
        {
            var fdusd = TradingPair.Parse("ETHFDUSD");
            Assert.Equal("ETH", fdusd.BaseAsset);
            Assert.Equal("FDUSD", fdusd.QuoteAsset);

            var usdc = TradingPair.Parse("BTCUSDC");
            Assert.Equal("USDC", usdc.QuoteAsset);
        });

        t.Case("Parse_常见小币种拆分_ARB_PAXG", () =>
        {
            Assert.Equal(("ARB", "USDT"), (TradingPair.Parse("ARBUSDT").BaseAsset, TradingPair.Parse("ARBUSDT").QuoteAsset));
            Assert.Equal("PAXG", TradingPair.Parse("PAXGUSDT").BaseAsset);
        });

        t.Case("TryParse_未知或畸形输入_失败", () =>
        {
            Assert.False(TradingPair.TryParse("XYZ", out _));
            Assert.False(TradingPair.TryParse("BTC", out _));
            Assert.False(TradingPair.TryParse("BTCXYZ", out _));
            Assert.False(TradingPair.TryParse("", out _));
            Assert.False(TradingPair.TryParse(null, out _));
        });

        t.Case("Parse_无法解析_抛FormatException", () =>
        {
            Assert.Throws<FormatException>(() => TradingPair.Parse("BTCXYZ"));
        });

        t.Case("值相等_按交易币与计价币比较", () =>
        {
            var a = TradingPair.Parse("BTCUSDT");
            var b = TradingPair.From("btc", "usdt");
            Assert.Equal(a, b);
            Assert.NotEqual(a, TradingPair.Parse("ETHUSDT"));
        });

        t.Case("JSON_以币安连写字符串往返", () =>
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new TradingPairJsonConverter());

            var pair = JsonSerializer.Deserialize<TradingPair>("\"ETHBTC\"", options);
            Assert.Equal(TradingPair.Parse("ETHBTC"), pair);

            var json = JsonSerializer.Serialize(TradingPair.Parse("ETHBTC"), options);
            Assert.Equal("\"ETHBTC\"", json);
        });

        t.Case("From_非法币种代码_抛异常", () =>
        {
            Assert.Throws<FormatException>(() => TradingPair.From("BT C", "USDT"));
            Assert.Throws<ArgumentException>(() => TradingPair.From("", "USDT"));
        });
    }
}
