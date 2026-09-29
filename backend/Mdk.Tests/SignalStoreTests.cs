using Mdk.Api.Analysis;
using Mdk.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mdk.Tests;

/// <summary>
/// 信号台账（SQLite）：标的拆成交易币/计价币两列、自然键去重、小数按定点文本存储、
/// 绩效回填、按各周期持有期筛出待评估项、旧 JSONL 一次性导入。
/// </summary>
public static class SignalStoreTests
{
    public static void Register(TestKit t)
    {
        t.Case("台账_写入与读回_交易对拆成两列", () =>
        {
            using var dir = new TempDir();
            using var store = Open(dir.Path);
            Assert.Equal(true, store.TryRecord(Entry(store, TradingPair.Parse("BTCUSDT"), time: 1_000_000)));
            Assert.Equal(1, store.Count);

            var row = Assert.Single(store.Snapshot());
            Assert.Equal("BTC", row.Pair.BaseAsset);
            Assert.Equal("USDT", row.Pair.QuoteAsset);
            Assert.Equal(MarketKind.Spot, row.Market);
            Assert.Equal("1h", row.Interval);
            Assert.Equal("缠论", row.Source);
            Assert.Equal(1_000_000L, row.Time);

            // 库内确实是两个独立列（而不是把连写串塞进一列）
            var cols = Query(dir.Path, "SELECT base_asset, quote_asset FROM signals LIMIT 1;");
            Assert.Equal("BTC", cols[0]);
            Assert.Equal("USDT", cols[1]);
            // 单行三列取列类型（辅助方法只读首行）
            var schema = Query(dir.Path, """
                SELECT (SELECT type FROM pragma_table_info('signals') WHERE name = 'price'),
                       (SELECT type FROM pragma_table_info('signals') WHERE name = 'stop_price'),
                       (SELECT type FROM pragma_table_info('signals') WHERE name = 'adx');
                """);
            Assert.Equal("TEXT", schema[0]);
            Assert.Equal("TEXT", schema[1]);
            Assert.Equal("TEXT", schema[2]);
        });

        t.Case("台账_自然键去重_同一信号只存一条", () =>
        {
            using var dir = new TempDir();
            using var store = Open(dir.Path);
            var pair = TradingPair.Parse("BTCUSDT");
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: 1_000_000)));
            Assert.Equal(false, store.TryRecord(Entry(store, pair, time: 1_000_000)));
            Assert.Equal(1, store.Count);

            // 时间不同、方向不同、确认级别不同 → 各自成条
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: 1_000_600)));
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: 1_000_000, side: "sell")));
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: 1_000_000, confirmed: false)));
            Assert.Equal(4, store.Count);
        });

        t.Case("台账_来源类型参与去重_live与回填样本互不覆盖", () =>
        {
            using var dir = new TempDir();
            using var store = Open(dir.Path);
            var pair = TradingPair.Parse("BTCUSDT");
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: 1_000_000, origin: "live")));
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: 1_000_000, origin: "backfill")));
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: 1_000_000, origin: "backfill-nosub")));
            Assert.Equal(false, store.TryRecord(Entry(store, pair, time: 1_000_000, origin: "backfill")));
            Assert.Equal(3, store.Count);
        });

        t.Case("台账_小数按定点文本存储_读回无损", () =>
        {
            using var dir = new TempDir();
            using var store = Open(dir.Path);
            var e = Entry(store, TradingPair.Parse("BTCUSDT"), time: 1_000_000);
            e.Price = decimal.Parse("78567.52");
            e.StopPrice = decimal.Parse("77773.15989705361");   // ATR 推导止损：28 位残渣
            e.Adx = decimal.Parse("27.4529");
            Assert.Equal(true, store.TryRecord(e));

            var row = Assert.Single(store.Snapshot());
            Assert.Equal(decimal.Parse("78567.52"), row.Price);
            Assert.Equal(decimal.Parse("77773.15989705"), row.StopPrice!.Value);   // 写入即八位定点
            Assert.Equal(decimal.Parse("27.4529"), row.Adx!.Value);

            // 库里是 TEXT（存 REAL 会把二进制浮点引回来）
            var stored = Query(dir.Path, "SELECT typeof(price), typeof(stop_price), price FROM signals LIMIT 1;");
            Assert.Equal("text", stored[0]);
            Assert.Equal("text", stored[1]);
            Assert.Equal("78567.52", stored[2]);
        });

        t.Case("台账_绩效回填_读回一致且不覆盖已评估结果", () =>
        {
            using var dir = new TempDir();
            using var store = Open(dir.Path);
            var e = Entry(store, TradingPair.Parse("ETHUSDT"), time: 1_000_000);
            Assert.Equal(true, store.TryRecord(e));

            store.MarkOutcome(e.Id, new SignalOutcome
            {
                Status = "ok", Ret = 0.002017245803354717m, Excess = -0.001260910533175679m,
                Mfe = 0.005229514690039849m, Mae = -0.00908161540545005m,
                StopHit = false, NetPositive = true, EvaluatedAt = 1_000_900,
            });

            var row = Assert.Single(store.Snapshot());
            Assert.Equal("ok", row.Outcome!.Status);
            Assert.Equal(0.00201725m, row.Outcome.Ret!.Value);      // 八位定点
            Assert.Equal(false, row.Outcome.StopHit!.Value);
            Assert.Equal(true, row.Outcome.NetPositive!.Value);
            Assert.Equal(1_000_900L, row.Outcome.EvaluatedAt!.Value);

            // 二次回填不覆盖（首次结论为准，避免统计被反复改写）
            store.MarkOutcome(e.Id, new SignalOutcome { Status = "expired", EvaluatedAt = 1_001_000 });
            var again = Assert.Single(store.Snapshot());
            Assert.Equal("ok", again.Outcome!.Status);
        });

        t.Case("台账_待评估筛选_按各周期持有期换算截止时间", () =>
        {
            using var dir = new TempDir();
            using var store = Open(dir.Path);
            var pair = TradingPair.Parse("BTCUSDT");
            const long now = 2_000_000_000L;
            const int horizon = 12;

            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: now - 11 * 3600, interval: "1h")));   // 未满
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: now - 12 * 3600, interval: "1h")));   // 期满
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: now - 47 * 3600, interval: "4h")));   // 4h 未满
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: now - 48 * 3600, interval: "4h")));   // 4h 期满
            Assert.Equal(true, store.TryRecord(Entry(store, pair, time: now - 12 * 3600, interval: "1d")));   // 1d 远未满

            var pending = store.PendingOutcomes(now, horizon, perIntervalLimit: 100)
                .Select(x => (x.Interval, x.Time))
                .OrderBy(x => x.Interval)
                .ToList();
            Assert.Equal(2, pending.Count);
            Assert.Equal(("1h", now - 12 * 3600), pending[0]);
            Assert.Equal(("4h", now - 48 * 3600), pending[1]);

            // 已评估的不再出现在待评估集合里
            var target = store.Snapshot().First(x => x.Interval == "1h" && x.Time == now - 12 * 3600);
            store.MarkOutcome(target.Id, new SignalOutcome { Status = "ok", Ret = 0.01m, EvaluatedAt = now });
            var after = store.PendingOutcomes(now, horizon, perIntervalLimit: 100).Select(x => x.Interval).ToList();
            Assert.Equal(1, after.Count);
            Assert.Equal("4h", after[0]);
        });

        t.Case("台账_旧JSONL导入_按连写切开并保留绩效", () =>
        {
            using var dir = new TempDir();
            File.WriteAllLines(Path.Combine(dir.Path, "signals.jsonl"),
            [
                """{"key":"spot|BTCUSDT|1h|RSI|buy|1788876000|1","market":"spot","symbol":"BTCUSDT","interval":"1h","source":"RSI","side":"buy","time":1788876000,"price":78567.52,"note":null,"stopPrice":77773.15989705361,"trendAligned":false,"confluence":null,"isConfirmed":true,"adx":null,"atrPct":null,"bandwidthPct":null,"recordedAt":1790604305,"origin":"live","outcome":{"status":"ok","ret":0.002017245803354717,"excess":-0.001260910533175679,"mfe":0.005229514690039849,"mae":-0.00908161540545005,"stopHit":false,"netPositive":true,"evaluatedAt":1790604363}}""",
                """{"key":"spot|BTCUSDT|1h|RSI|buy|1788876000|1","market":"spot","symbol":"BTCUSDT","interval":"1h","source":"RSI","side":"buy","time":1788876000,"price":78567.52,"isConfirmed":true,"recordedAt":1790604305,"origin":"live"}""",
                """{"key":"spot|???|1h|RSI|buy|1788876000|1","market":"spot","symbol":"???","interval":"1h","source":"RSI","side":"buy","time":1788876000,"price":1,"isConfirmed":true,"recordedAt":1790604305,"origin":"live"}""",
                """{"market":"spot","symbol":"ETHUSDT","interval":"4h","source":"缠论","side":"sell","time":1788876000,"price":2674.17,"isConfirmed":true,"recordedAt":1790604305,"origin":"backfill"}""",
            ]);

            using (var store = Open(dir.Path))
            {
                // 4 行里：第 2 行与第 1 行同键（去重）、第 3 行标的切不开（跳过）→ 入库 2 条
                Assert.Equal(2, store.Count);
                var btc = Assert.Single(store.Snapshot().Where(x => x.Pair.BaseAsset == "BTC").ToList());
                Assert.Equal("USDT", btc.Pair.QuoteAsset);
                Assert.Equal(decimal.Parse("78567.52"), btc.Price);
                Assert.Equal(decimal.Parse("77773.15989705"), btc.StopPrice!.Value);
                Assert.Equal("ok", btc.Outcome!.Status);
                Assert.Equal(0.00201725m, btc.Outcome.Ret!.Value);

                var eth = Assert.Single(store.Snapshot().Where(x => x.Pair.BaseAsset == "ETH").ToList());
                Assert.Equal("4h", eth.Interval);
                Assert.Equal("backfill", eth.Origin);
                Assert.True(eth.Outcome is null);
            }

            // 再次打开不再重复导入（表非空即跳过）
            using (var store2 = Open(dir.Path))
            {
                Assert.Equal(2, store2.Count);
            }
        });
    }

    private static SignalStore Open(string dir) => new(dir, NullLogger<SignalStore>.Instance);

    /// <summary>绕开台账直接读库（校验落盘形态：列结构、存储类型、原始文本）。</summary>
    private static List<string> Query(string dir, string sql)
    {
        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "signals.db") }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        if (!reader.Read()) return values;
        for (var i = 0; i < reader.FieldCount; i++)
            values.Add(reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString()!);
        return values;
    }

    private static SignalEntry Entry(
        SignalStore store, TradingPair pair, long time, string interval = "1h",
        string side = "buy", bool confirmed = true, string origin = "live") => new()
    {
        Market = MarketKind.Spot,
        Pair = pair,
        Interval = interval,
        Source = "缠论",
        Side = side,
        Time = time,
        Price = 100m,
        StopPrice = 99m,
        IsConfirmed = confirmed,
        Adx = 20m,
        AtrPct = 1.5m,
        BandwidthPct = 3m,
        RecordedAt = time,
        Origin = origin,
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mdk-store-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }
}
