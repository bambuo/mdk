using System.Text.Json;
using Mdk.Api.Notify;

namespace Mdk.Tests;

/// <summary>
/// 飞书提醒：签名（已知答案向量，独立实现算得）、消息卡片形状、同向冷却、未配置时惰性。
/// </summary>
public static class FeishuNotifierTests
{
    public static void Register(TestKit t)
    {
        t.Case("飞书签名_已知答案向量（Python 独立实现算得）", () =>
        {
            // 向量由 Python 的 hmac/hashlib/base64 独立计算：hmac(sha256, key="1790827000\ntest-secret-abc123", msg=b"")
            Assert.Equal("V2KSAYi2cOIYGyzPG82Wb6rQ3rhT2vQUHceEtRhTBpY=",
                FeishuSign.Compute(1_790_827_000L, "test-secret-abc123"));
        });

        t.Case("飞书签名_密钥或时间戳变化则签名不同", () =>
        {
            var a = FeishuSign.Compute(1_790_827_000L, "s1");
            Assert.NotEqual(a, FeishuSign.Compute(1_790_827_000L, "s2"));
            Assert.NotEqual(a, FeishuSign.Compute(1_790_827_001L, "s1"));
            Assert.Equal(a, FeishuSign.Compute(1_790_827_000L, "s1"));   // 确定性
        });

        t.Case("飞书消息_买点绿卡_字段与脚注齐全", () =>
        {
            using var doc = JsonDocument.Parse(FeishuMessage.BuildBody(Signal("buy", "3买"), 1_790_827_000L, null));
            var root = doc.RootElement;
            Assert.Equal("interactive", root.GetProperty("msg_type").GetString());
            var card = root.GetProperty("card");
            Assert.Equal("green", card.GetProperty("header").GetProperty("template").GetString());
            Assert.Equal("BTC/USDT 1h 买点", card.GetProperty("header").GetProperty("title").GetProperty("content").GetString());
            var text = card.GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString()!;
            Assert.Contains("3买", text);
            Assert.Contains("83000", text);
            Assert.Contains("82800", text);
            var all = card.GetRawText();
            Assert.Contains("不构成投资建议", all);
            Assert.Contains("自检示例", all);
        });

        t.Case("飞书消息_卖点红卡", () =>
        {
            using var doc = JsonDocument.Parse(FeishuMessage.BuildBody(Signal("sell", "1卖"), 1_790_827_000L, null));
            var card = doc.RootElement.GetProperty("card");
            Assert.Equal("red", card.GetProperty("header").GetProperty("template").GetString());
            Assert.Equal("BTC/USDT 1h 卖点", card.GetProperty("header").GetProperty("title").GetProperty("content").GetString());
        });

        t.Case("飞书消息_未配置密钥时不带timestamp与sign", () =>
        {
            var body = FeishuMessage.BuildBody(Signal("buy", "3买"), 1_790_827_000L, secret: null);
            Assert.Equal(false, body.Contains("\"sign\""));
            Assert.Equal(false, body.Contains("\"timestamp\""));
        });

        t.Case("飞书消息_配置密钥时带秒级timestamp与sign", () =>
        {
            using var doc = JsonDocument.Parse(FeishuMessage.BuildBody(Signal("buy", "3买"), 1_790_827_000L, "sec"));
            var root = doc.RootElement;
            Assert.Equal("1790827000", root.GetProperty("timestamp").GetString());   // 秒级，非毫秒
            Assert.Equal(FeishuSign.Compute(1_790_827_000L, "sec"), root.GetProperty("sign").GetString());
        });

        t.Case("飞书消息_中文不被转义为unicode转义", () =>
        {
            var body = FeishuMessage.BuildBody(Signal("buy", "3买"), 1_790_827_000L, null);
            Assert.Contains("买点", body);      // 用 UnsafeRelaxedJsonEscaping：可读且长度更小
        });

        t.Case("飞书冷却_同向窗口内拦下_反向放行_超窗放行", () =>
        {
            var cd = new SignalCooldown(30);
            Assert.Equal(true, cd.TryAcquire("BTCUSDT", "1h", "buy", 1_000_000));
            Assert.Equal(false, cd.TryAcquire("BTCUSDT", "1h", "buy", 1_000_600));       // 10 分钟后仍拦
            Assert.Equal(true, cd.TryAcquire("BTCUSDT", "1h", "sell", 1_000_600));       // 反向放行
            Assert.Equal(true, cd.TryAcquire("BTCUSDT", "1h", "buy", 1_000_000 + 30 * 60)); // 满 30 分钟放行
            Assert.Equal(true, cd.TryAcquire("ETHUSDT", "1h", "buy", 1_000_000));        // 不同标的不互相影响
            Assert.Equal(true, cd.TryAcquire("BTCUSDT", "15m", "buy", 1_000_000));       // 不同周期不互相影响
        });
    }

    private static NotifiableSignal Signal(string side, string kind) => new(
        Market: "spot", Symbol: "BTCUSDT", BaseAsset: "BTC", QuoteAsset: "USDT", Interval: "1h",
        Kind: kind, Side: side, Time: 1_790_827_000L, Price: 83000m, StopPrice: 82800m,
        Note: "自检示例：MACD 面积背驰 0.5");
}
