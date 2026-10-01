using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mdk.Api.Analysis;

/// <summary>
/// 一分钟、一个标的的清算聚合。
///
/// **必读的完整性缺陷**：币安的 `!forceOrder@arr` 只推"每秒每标的最大的那一笔"快照，
/// 因此这里的量**系统性少于真实清算量**（不是抽样估计，而是确定的少计）。用途是相对比较
/// （哪个价位、哪个时段被清算得更凶、哪一侧在挨打），**不得当作清算总额使用**。
/// </summary>
public sealed record LiquidationBucket(
    string Symbol,
    long Minute,
    int Count,
    decimal BuyQty,
    decimal SellQty,
    decimal BuyNotional,
    decimal SellNotional,
    /// <summary>该分钟内最大的单笔清算名义额（用于识别"最凶的那一笔"）。</summary>
    decimal MaxNotional);

/// <summary>清算汇总。SELL 侧 = 多头被强平（卖出平多），BUY 侧 = 空头被强平。</summary>
public sealed record LiquidationSummary(
    int Count,
    decimal BuyNotional,
    decimal SellNotional,
    decimal MaxSingleNotional)
{
    public decimal TotalNotional => BuyNotional + SellNotional;

    /// <summary>多头被强平占比（0~1）：>0.5 表示这段时间以多头挨打为主。</summary>
    public decimal LongLiquidatedShare =>
        TotalNotional > 0 ? Math.Round(SellNotional / TotalNotional, 4) : 0m;

    public bool IsEmpty => Count == 0;
}

/// <summary>币安强平推送的解析（纯函数，可独立测试）。</summary>
public static class LiquidationMessage
{
    /// <summary>解析 <c>!forceOrder@arr</c> 的一帧；非清算帧或字段缺失返回 null。</summary>
    public static (string Symbol, string Side, decimal Qty, decimal Price, long TimeMs)? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("o", out var order)) return null;
            if (order.TryGetProperty("e", out var eventName) && eventName.GetString() != "forceOrder") return null;

            var symbol = order.GetProperty("s").GetString();
            var side = order.GetProperty("S").GetString();
            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(side)) return null;

            // 成交量优先取已成交 z；成交均价取 ap（缺失回落到 p）
            var qty = order.TryGetProperty("z", out var filled) ? Num(filled) : Num(order.GetProperty("q"));
            var price = order.TryGetProperty("ap", out var avg) ? Num(avg) : Num(order.GetProperty("p"));
            var timeMs = order.TryGetProperty("T", out var time) ? time.GetInt64() : 0L;
            return qty > 0 && price > 0 && timeMs > 0 ? (symbol, side, qty, price, timeMs) : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static decimal Num(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDecimal(),
        JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var value) ? value : 0m,
        _ => 0m,
    };
}

/// <summary>
/// 清算聚合的落库（SQLite：<c>data/liquidations.db</c>，独立于信号台账——写入频率与保留期都不同）。
/// 小数按项目口径以 TEXT 存储；主键 (symbol, minute) 保证同一分钟累加而非重复。
/// </summary>
public sealed class LiquidationStore : IDisposable
{
    private readonly Lock _sync = new();
    private readonly SqliteConnection _conn;
    private readonly ILogger<LiquidationStore> _logger;

    public LiquidationStore(string dataDirectory, ILogger<LiquidationStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "liquidations.db");
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA busy_timeout=5000;");
        Exec("""
             CREATE TABLE IF NOT EXISTS liquidations (
               symbol TEXT NOT NULL, minute INTEGER NOT NULL, count INTEGER NOT NULL,
               buy_qty TEXT NOT NULL, sell_qty TEXT NOT NULL,
               buy_notional TEXT NOT NULL, sell_notional TEXT NOT NULL,
               max_notional TEXT NOT NULL,
               PRIMARY KEY (symbol, minute));
             """);
        Exec("CREATE INDEX IF NOT EXISTS idx_liquidations_minute ON liquidations(minute);");
        _logger.LogInformation("清算数据存储就绪：{Path}", dbPath);
    }

    /// <summary>
    /// 累加写入：同一 (标的, 分钟) 重复写入即累加（重启不丢不重）。
    /// 累加在 C# 侧用 decimal 完成（读—改—写），不在 SQL 里用浮点求和——业务数值不用浮点近似。
    /// </summary>
    public void Add(IReadOnlyList<LiquidationBucket> buckets)
    {
        if (buckets.Count == 0) return;
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var bucket in buckets)
            {
                var existing = Read(tx, bucket.Symbol, bucket.Minute);
                var merged = existing is null
                    ? bucket
                    : existing with
                    {
                        Count = existing.Count + bucket.Count,
                        BuyQty = existing.BuyQty + bucket.BuyQty,
                        SellQty = existing.SellQty + bucket.SellQty,
                        BuyNotional = existing.BuyNotional + bucket.BuyNotional,
                        SellNotional = existing.SellNotional + bucket.SellNotional,
                        MaxNotional = Math.Max(existing.MaxNotional, bucket.MaxNotional),
                    };
                Write(tx, merged);
            }

            tx.Commit();
        }
    }

    /// <summary>自 fromMinute（含）起的汇总（小数在 C# 侧用 decimal 求和）。</summary>
    public LiquidationSummary Summary(string symbol, long fromMinute)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT count, buy_notional, sell_notional, max_notional
                FROM liquidations WHERE symbol = $symbol AND minute >= $from;
                """;
            cmd.Parameters.AddWithValue("$symbol", symbol);
            cmd.Parameters.AddWithValue("$from", fromMinute);
            using var reader = cmd.ExecuteReader();
            var count = 0;
            var buy = 0m;
            var sell = 0m;
            var max = 0m;
            while (reader.Read())
            {
                count += reader.GetInt32(0);
                buy += Parse(reader.GetString(1));
                sell += Parse(reader.GetString(2));
                max = Math.Max(max, Parse(reader.GetString(3)));
            }

            return new LiquidationSummary(count, buy, sell, max);
        }
    }

    private LiquidationBucket? Read(SqliteTransaction tx, string symbol, long minute)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT count, buy_qty, sell_qty, buy_notional, sell_notional, max_notional
            FROM liquidations WHERE symbol = $symbol AND minute = $minute;
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$minute", minute);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new LiquidationBucket(symbol, minute, reader.GetInt32(0),
            Parse(reader.GetString(1)), Parse(reader.GetString(2)),
            Parse(reader.GetString(3)), Parse(reader.GetString(4)), Parse(reader.GetString(5)));
    }

    private void Write(SqliteTransaction tx, LiquidationBucket bucket)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO liquidations (symbol, minute, count, buy_qty, sell_qty, buy_notional, sell_notional, max_notional)
            VALUES ($symbol, $minute, $count, $buyQty, $sellQty, $buyNotional, $sellNotional, $maxNotional)
            ON CONFLICT(symbol, minute) DO UPDATE SET
              count = excluded.count, buy_qty = excluded.buy_qty, sell_qty = excluded.sell_qty,
              buy_notional = excluded.buy_notional, sell_notional = excluded.sell_notional,
              max_notional = excluded.max_notional;
            """;
        cmd.Parameters.AddWithValue("$symbol", bucket.Symbol);
        cmd.Parameters.AddWithValue("$minute", bucket.Minute);
        cmd.Parameters.AddWithValue("$count", bucket.Count);
        cmd.Parameters.AddWithValue("$buyQty", Text(bucket.BuyQty));
        cmd.Parameters.AddWithValue("$sellQty", Text(bucket.SellQty));
        cmd.Parameters.AddWithValue("$buyNotional", Text(bucket.BuyNotional));
        cmd.Parameters.AddWithValue("$sellNotional", Text(bucket.SellNotional));
        cmd.Parameters.AddWithValue("$maxNotional", Text(bucket.MaxNotional));
        cmd.ExecuteNonQuery();
    }

    /// <summary>清理早于 beforeMinute 的数据（默认保留 30 天）。</summary>
    public int Purge(long beforeMinute)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM liquidations WHERE minute < $before;";
            cmd.Parameters.AddWithValue("$before", beforeMinute);
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal Parse(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0m;
}
