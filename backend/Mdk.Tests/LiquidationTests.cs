using Mdk.Api.Analysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mdk.Tests;

/// <summary>
/// 强平采集的解析与落库口径。**完整性缺陷必须记住**：币安只推"每秒每标的最大一笔"快照，
/// 采集量系统性少于真实清算量——相对比较可用，总额不可用。
/// </summary>
public static class LiquidationTests
{
    public static void Register(TestKit t)
    {
        t.Case("清算_解析强平帧_用实盘抓到的原帧", () =>
        {
            // 2026-10-01 从 wss://fstream.binance.com/market/stream?streams=!forceOrder@arr 实际抓取
            const string json = """
                {"stream":"!forceOrder@arr","data":{"e":"forceOrder","E":1790845665206,
                "o":{"s":"BTCUSDT","S":"BUY","o":"LIMIT","f":"IOC","q":"0.001","p":"83930.70",
                     "ap":"83598.50","X":"FILLED","l":"0.001","z":"0.001","T":1790845665206,"ps":"BTCUSDT","st":1}}}
                """;

            var parsed = LiquidationMessage.TryParse(json);

            Assert.True(parsed is not null, "应能解析实盘强平帧");
            Assert.Equal("BTCUSDT", parsed!.Value.Symbol);
            Assert.Equal("BUY", parsed.Value.Side);
            Assert.Equal(0.001m, parsed.Value.Qty);        // 取已成交 z
            Assert.Equal(83598.50m, parsed.Value.Price);   // 取成交均价 ap（而非委托价 p=83930.70）
            Assert.Equal(1_790_845_665_206L, parsed.Value.TimeMs);
        });

        t.Case("清算_非清算帧_缺字段_坏JSON_都返回空", () =>
        {
            Assert.True(LiquidationMessage.TryParse("""{"stream":"btcusdt@markPrice@1s","data":{"e":"markPriceUpdate"}}""") is null);
            Assert.True(LiquidationMessage.TryParse("{}") is null);
            Assert.True(LiquidationMessage.TryParse("不是 JSON") is null);
            // 成交量为 0（未成交）不得入库
            Assert.True(LiquidationMessage.TryParse("""
                {"data":{"e":"forceOrder","o":{"s":"BTCUSDT","S":"BUY","q":"1","p":"100","z":"0","T":1700000000000}}}
                """) is null);
        });

        t.Case("清算_同分钟累加_多头被清占比_按保留期清理", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "mdk-liq-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new LiquidationStore(dir, NullLogger<LiquidationStore>.Instance))
                {
                    store.Add([new LiquidationBucket("BTCUSDT", 100, 2, 0m, 3m, 0m, 300m, 200m)]);
                    store.Add([new LiquidationBucket("BTCUSDT", 100, 1, 1m, 0m, 100m, 0m, 100m)]);   // 同分钟：累加
                    store.Add([new LiquidationBucket("ETHUSDT", 100, 1, 1m, 0m, 50m, 0m, 50m)]);    // 另一标的

                    var btc = store.Summary("BTCUSDT", 0);
                    Assert.Equal(3, btc.Count);
                    Assert.Equal(300m, btc.SellNotional);
                    Assert.Equal(100m, btc.BuyNotional);
                    Assert.Equal(0.75m, btc.LongLiquidatedShare);   // 卖侧（多头被强平）占 3/4
                    Assert.Equal(200m, btc.MaxSingleNotional);

                    var eth = store.Summary("ETHUSDT", 0);
                    Assert.Equal(1, eth.Count);
                    Assert.Equal(50m, eth.TotalNotional);

                    Assert.True(store.Summary("SOLUSDT", 0).IsEmpty, "没有数据的标的应为空（而不是 0 值混入）");
                    Assert.Equal(0, store.Purge(beforeMinute: 100), "清理边界：minute=100 不在清理范围内");
                    Assert.Equal(2, store.Purge(beforeMinute: 101), "清理早于 101 的行");
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        });
    }
}
