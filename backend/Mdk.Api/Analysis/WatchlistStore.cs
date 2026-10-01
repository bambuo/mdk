using System.Globalization;
using Microsoft.Data.Sqlite;
using Mdk.Api.Domain;

namespace Mdk.Api.Analysis;

/// <summary>一个监控条目：某市场下的交易对 + 要监控的周期集合（用户显式指定，不再从"最近请求"推断）。</summary>
public sealed record WatchItem(
    MarketKind Market,
    TradingPair Pair,
    IReadOnlyList<string> Intervals,
    bool Enabled,
    long CreatedAt);

/// <summary>
/// 监控列表的持久化（SQLite 表 <c>watchlist</c>，与信号台账同一个 <c>data/signals.db</c>）。
/// 表在应用启动时按 <c>CREATE TABLE IF NOT EXISTS</c> 建立（与 <see cref="SignalStore"/> 同一套做法，
/// signals.db 是应用自管的本地库，不是需要人工迁移的共享库）；标的按项目口径拆成 base_asset / quote_asset 两列。
/// 周期序列存逗号分隔文本（SQLite 无数组类型；周期取自白名单，不含逗号）。
/// </summary>
public sealed class WatchlistStore : IDisposable
{
    private readonly Lock _sync = new();
    private readonly SqliteConnection _conn;
    private readonly ILogger<WatchlistStore> _logger;

    public WatchlistStore(string dataDirectory, ILogger<WatchlistStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "signals.db");
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA busy_timeout=5000;");
        CreateSchema();
        _logger.LogInformation("监控列表存储就绪：{Path}", dbPath);
    }

    public IReadOnlyList<WatchItem> All()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT market, base_asset, quote_asset, intervals, enabled, created_at FROM watchlist ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            var items = new List<WatchItem>();
            while (reader.Read())
            {
                if (!MarketKindExtensions.TryParse(reader.GetString(0), out var market)) continue;
                var pair = TradingPair.From(reader.GetString(1), reader.GetString(2));
                var intervals =
                    NormalizeIntervals(reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries));
                if (intervals.Count == 0) continue;
                items.Add(new WatchItem(market, pair, intervals, reader.GetInt64(4) != 0, reader.GetInt64(5)));
            }

            return items;
        }
    }

    /// <summary>新增或更新（同一市场 + 交易对唯一）：替换周期集合，保留创建时间与启用状态。</summary>
    public WatchItem Upsert(MarketKind market, TradingPair pair, IReadOnlyList<string> intervals)
    {
        var normalized = NormalizeIntervals(intervals);
        lock (_sync)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                                  INSERT INTO watchlist (market, base_asset, quote_asset, intervals, enabled, created_at)
                                  VALUES ($market, $base, $quote, $intervals, 1, $created)
                                  ON CONFLICT (market, base_asset, quote_asset)
                                  DO UPDATE SET intervals = excluded.intervals;
                                  """;
                cmd.Parameters.AddWithValue("$market", Market(market));
                cmd.Parameters.AddWithValue("$base", pair.BaseAsset);
                cmd.Parameters.AddWithValue("$quote", pair.QuoteAsset);
                cmd.Parameters.AddWithValue("$intervals", string.Join(',', normalized));
                cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }

            return ReadOne(market, pair) ?? new WatchItem(market, pair, normalized, true, 0);
        }
    }

    public bool Remove(MarketKind market, TradingPair pair)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM watchlist WHERE market = $market AND base_asset = $base AND quote_asset = $quote;";
            AddKey(cmd, market, pair);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>切换启用状态；返回切换后的状态（条目不存在返回 null）。</summary>
    public bool? Toggle(MarketKind market, TradingPair pair)
    {
        lock (_sync)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                                  UPDATE watchlist SET enabled = 1 - enabled
                                  WHERE market = $market AND base_asset = $base AND quote_asset = $quote;
                                  """;
                AddKey(cmd, market, pair);
                if (cmd.ExecuteNonQuery() == 0) return null;
            }

            return ReadOne(market, pair)?.Enabled;
        }
    }

    /// <summary>周期归一：只保留白名单周期、去重，并按 MarketIntervals.All 的规范顺序排列。</summary>
    public static IReadOnlyList<string> NormalizeIntervals(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return [];
        var set = new HashSet<string>(
            raw.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.Ordinal);
        return MarketIntervals.All.Where(set.Contains).ToList();
    }

    private WatchItem? ReadOne(MarketKind market, TradingPair pair)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT intervals, enabled, created_at FROM watchlist WHERE market = $market AND base_asset = $base AND quote_asset = $quote;";
        AddKey(cmd, market, pair);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var intervals = NormalizeIntervals(reader.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries));
        return new WatchItem(market, pair, intervals, reader.GetInt64(1) != 0, reader.GetInt64(2));
    }

    private void CreateSchema() => Exec("""
                                        CREATE TABLE IF NOT EXISTS watchlist (
                                            id           INTEGER PRIMARY KEY AUTOINCREMENT,
                                            market       TEXT    NOT NULL,
                                            base_asset   TEXT    NOT NULL,
                                            quote_asset  TEXT    NOT NULL,
                                            intervals    TEXT    NOT NULL,
                                            enabled      INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
                                            created_at   INTEGER NOT NULL,
                                            UNIQUE (market, base_asset, quote_asset)
                                        );
                                        """);

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddKey(SqliteCommand cmd, MarketKind market, TradingPair pair)
    {
        cmd.Parameters.AddWithValue("$market", Market(market));
        cmd.Parameters.AddWithValue("$base", pair.BaseAsset);
        cmd.Parameters.AddWithValue("$quote", pair.QuoteAsset);
    }

    private static string Market(MarketKind market) => market.ToString().ToLowerInvariant();

    public void Dispose() => _conn.Dispose();
}
