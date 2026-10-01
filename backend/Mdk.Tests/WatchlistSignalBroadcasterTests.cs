using Mdk.Api.Analysis;
using Mdk.Api.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace Mdk.Tests;

/// <summary>
/// 监控列表信号广播器（SSE 数据源）测试：过滤口径——
/// 只推「监控列表内、已确认、缠论、origin=live」的信号；
/// 非监控标的 / 回填来源 / 盘中预警 / 非缠论来源 / 停用条目 一律不推。
/// </summary>
public static class WatchlistSignalBroadcasterTests
{
    public static void Register(TestKit t)
    {
        t.Case("广播器_监控列表内新入库已确认买卖点_推送给订阅者", () =>
        {
            using var env = new Env();
            env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id = hub.Subscribe(out var reader);

            env.Signals.TryRecord(Entry("BTCUSDT", kind: "3买", origin: "live", confirmed: true));

            Assert.Equal(true, reader.TryRead(out var payload));
            // 中文会被 JSON 转义（\u4E70），因此解析后按字段断言
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            Assert.Equal("BTCUSDT", root.GetProperty("symbol").GetString());
            Assert.Equal("3买", root.GetProperty("kind").GetString());
            Assert.Equal("buy", root.GetProperty("side").GetString());
            Assert.Equal("spot", root.GetProperty("market").GetString());   // market 是市场字段；origin 过滤在推送前完成
        });

        t.Case("广播器_非监控标的_不推送", () =>
        {
            using var env = new Env();
            env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id = hub.Subscribe(out var reader);

            env.Signals.TryRecord(Entry("ETHUSDT", origin: "live", confirmed: true));

            Assert.Equal(false, reader.TryRead(out _));
        });

        t.Case("广播器_回填来源_不推送（历史批量入库不打扰在线客户端）", () =>
        {
            using var env = new Env();
            env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id = hub.Subscribe(out var reader);

            env.Signals.TryRecord(Entry("BTCUSDT", origin: "backfill", confirmed: true));

            Assert.Equal(false, reader.TryRead(out _));
        });

        t.Case("广播器_盘中预警_不推送", () =>
        {
            using var env = new Env();
            env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id = hub.Subscribe(out var reader);

            env.Signals.TryRecord(Entry("BTCUSDT", origin: "live", confirmed: false));

            Assert.Equal(false, reader.TryRead(out _));
        });

        t.Case("广播器_非缠论来源_不推送", () =>
        {
            using var env = new Env();
            env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id = hub.Subscribe(out var reader);

            env.Signals.TryRecord(Entry("BTCUSDT", origin: "live", confirmed: true, source: "EMA"));

            Assert.Equal(false, reader.TryRead(out _));
        });

        t.Case("广播器_监控条目已停用_不推送", () =>
        {
            using var env = new Env();
            var item = env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            env.Watchlist.Toggle(item.Market, item.Pair);   // 停用
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id = hub.Subscribe(out var reader);

            env.Signals.TryRecord(Entry("BTCUSDT", origin: "live", confirmed: true));

            Assert.Equal(false, reader.TryRead(out _));
        });

        t.Case("广播器_多订阅者_各得一份", () =>
        {
            using var env = new Env();
            env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id1 = hub.Subscribe(out var r1);
            var id2 = hub.Subscribe(out var r2);

            env.Signals.TryRecord(Entry("BTCUSDT", origin: "live", confirmed: true));

            Assert.Equal(true, r1.TryRead(out _));
            Assert.Equal(true, r2.TryRead(out _));
        });

        t.Case("广播器_退订后_不再接收", () =>
        {
            using var env = new Env();
            env.Watchlist.Upsert(MarketKind.Spot, TradingPair.Parse("BTCUSDT"), ["1h"]);
            using var hub = new WatchlistSignalBroadcaster(env.Watchlist, env.Signals);
            var id = hub.Subscribe(out var reader);
            hub.Unsubscribe(id);

            env.Signals.TryRecord(Entry("BTCUSDT", origin: "live", confirmed: true));

            Assert.Equal(false, reader.TryRead(out _));
        });
    }

    /// <summary>测试环境：临时目录里的监控列表与台账。</summary>
    private sealed class Env : IDisposable
    {
        public WatchlistStore Watchlist { get; }
        public SignalStore Signals { get; }

        public Env()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mdk-broadcast-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Dir = dir;
            Watchlist = new WatchlistStore(dir, NullLogger<WatchlistStore>.Instance);
            Signals = new SignalStore(dir, NullLogger<SignalStore>.Instance);
        }

        private string Dir { get; }

        public void Dispose()
        {
            Signals.Dispose();
            Watchlist.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    private static SignalEntry Entry(string symbol, string origin, bool confirmed, string source = "缠论", string kind = "3买") => new()
    {
        Market = MarketKind.Spot,
        Pair = TradingPair.Parse(symbol),
        Interval = "1h",
        Source = source,
        Kind = kind,
        Side = "buy",
        Time = 1_790_000_000,
        Price = 83000m,
        StopPrice = 82800m,
        IsConfirmed = confirmed,
        RecordedAt = 1_790_000_010,
        Origin = origin,
    };
}
