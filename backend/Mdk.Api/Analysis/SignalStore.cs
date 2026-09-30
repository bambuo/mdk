using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mdk.Api.Domain;
using Microsoft.Data.Sqlite;

namespace Mdk.Api.Analysis;

/// <summary>信号的事后绩效（由 SignalOutcomeService 在持有期满后回填）。</summary>
public sealed class SignalOutcome
{
    /// <summary>ok=已评估；expired=信号过旧超出可用K线范围，无法评估。</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "ok";
    [JsonPropertyName("ret")] public decimal? Ret { get; set; }
    [JsonPropertyName("excess")] public decimal? Excess { get; set; }
    [JsonPropertyName("mfe")] public decimal? Mfe { get; set; }
    [JsonPropertyName("mae")] public decimal? Mae { get; set; }
    [JsonPropertyName("stopHit")] public bool? StopHit { get; set; }
    [JsonPropertyName("netPositive")] public bool? NetPositive { get; set; }
    [JsonPropertyName("evaluatedAt")] public long? EvaluatedAt { get; set; }
}

/// <summary>
/// 一条信号台账记录（signals 表的一行）。
/// 标的以 <see cref="TradingPair"/> 值对象承载——落库时拆成 base_asset / quote_asset 两个独立字段，
/// 与全项目的交易对口径一致（不用连写的裸字符串当主表示）。
/// </summary>
public sealed class SignalEntry
{
    /// <summary>台账行号（自增主键），用于回填绩效时定位。</summary>
    public long Id { get; set; }
    public MarketKind Market { get; set; }
    public TradingPair Pair { get; set; }
    public string Interval { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>买卖点类别（缠论："1买"…"3卖"）；其他来源为 null。用于按语境给出经验可信度。</summary>
    public string? Kind { get; set; }
    public string Side { get; set; } = "";
    /// <summary>信号记账的K线时间（该根收盘后信号才可知，入场价取该根收盘价）。</summary>
    public long Time { get; set; }
    public decimal Price { get; set; }
    /// <summary>信号说明原文（含位点价位/触碰次数等，便于事后审计）。</summary>
    public string? Note { get; set; }
    public decimal? StopPrice { get; set; }
    /// <summary>结构参考价（缠论信号）：用于事后复核"入场是否已经追高"（滞后 ÷ 风险单位）。</summary>
    public decimal? ReferencePrice { get; set; }
    /// <summary>级别共振标签（aligned/counter/none，仅缠论信号）。</summary>
    public string? Confluence { get; set; }
    public bool IsConfirmed { get; set; }
    /// <summary>信号发生时的市场状态（用于按状态分组统计，如 ADX≥25 的趋势市 vs 震荡市）。</summary>
    public decimal? Adx { get; set; }
    public decimal? AtrPct { get; set; }
    public decimal? BandwidthPct { get; set; }
    /// <summary>联合打分（结构共振 + 技术指标，等权 0–6）；旧行/非缠论行为 null。</summary>
    public int? JointScore { get; set; }
    public long RecordedAt { get; set; }
    /// <summary>来源：live=实时分析路径写入；backfill=历史回填（事后一次性生成，统计时需区分）。</summary>
    public string Origin { get; set; } = "live";
    public SignalOutcome? Outcome { get; set; }
}

/// <summary>
/// 信号台账（SQLite，data/signals.db）。一行一条信号，唯一约束去重
/// （同市场/交易对/周期/来源/方向/信号K线/确认级别/来源类型只存一条——
/// 末项 origin 参与去重是为了让同一信号在 live / backfill / backfill-nosub 下各存一条，
/// 次级别确认 A/B 对照与"在线样本 vs 回填样本"的分组统计都依赖它）；
/// 标的拆为 base_asset + quote_asset 两列；绩效列由后台服务在持有期满后 UPDATE 回填。
///
/// **小数一律存 TEXT**（与 JSON 同口径的定点文本，见 <see cref="DecimalJsonConverter.Format"/>）：
/// SQLite 没有 decimal 类型，存 REAL 会把二进制浮点引回来，与"钱与点位不用浮点近似"的口径冲突；
/// 因此 SQL 侧不做数值聚合，统计一律取回 C# 用 decimal 计算。
/// </summary>
public sealed class SignalStore : IDisposable
{
    private readonly Lock _sync = new();
    private readonly SqliteConnection _conn;
    private readonly ILogger<SignalStore> _logger;
    private readonly string _legacyJsonlPath;

    public SignalStore(string dataDirectory, ILogger<SignalStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _legacyJsonlPath = Path.Combine(dataDirectory, "signals.jsonl");
        var dbPath = Path.Combine(dataDirectory, "signals.db");
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _conn.Open();
        // WAL：回填绩效的写与统计查询的读可并发；台账是单进程写入，无需额外锁表策略
        using (var mode = _conn.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode=WAL;";
            mode.ExecuteScalar();
        }
        using (var sync = _conn.CreateCommand())
        {
            sync.CommandText = "PRAGMA synchronous=NORMAL;";
            sync.ExecuteNonQuery();
        }
        CreateSchema();
        ImportLegacyJsonlIfEmpty();
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM signals;";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>记录一条信号；自然键重复（同一信号已存在）返回 false。</summary>
    public bool TryRecord(SignalEntry e)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO signals
                    (market, base_asset, quote_asset, interval, source, kind, side, signal_time, is_confirmed,
                     price, stop_price, reference_price, note, confluence, adx, atr_pct,
                     bandwidth_pct, origin, recorded_at, joint_score)
                VALUES
                    ($market, $base, $quote, $interval, $source, $kind, $side, $time, $confirmed,
                     $price, $stop, $reference, $note, $confluence, $adx, $atrPct, $bandwidth,
                     $origin, $recordedAt, $jointScore);
                """;
            cmd.Parameters.AddWithValue("$market", MarketKey(e.Market));
            cmd.Parameters.AddWithValue("$base", e.Pair.BaseAsset);
            cmd.Parameters.AddWithValue("$quote", e.Pair.QuoteAsset);
            cmd.Parameters.AddWithValue("$interval", e.Interval);
            cmd.Parameters.AddWithValue("$source", e.Source);
            cmd.Parameters.AddWithValue("$kind", e.Kind ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$side", e.Side);
            cmd.Parameters.AddWithValue("$time", e.Time);
            cmd.Parameters.AddWithValue("$confirmed", e.IsConfirmed ? 1 : 0);
            cmd.Parameters.AddWithValue("$price", ToText(e.Price));
            cmd.Parameters.AddWithValue("$stop", ToText(e.StopPrice));
            cmd.Parameters.AddWithValue("$reference", ToText(e.ReferencePrice));
            cmd.Parameters.AddWithValue("$note", e.Note ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$confluence", e.Confluence ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$adx", ToText(e.Adx));
            cmd.Parameters.AddWithValue("$atrPct", ToText(e.AtrPct));
            cmd.Parameters.AddWithValue("$bandwidth", ToText(e.BandwidthPct));
            cmd.Parameters.AddWithValue("$origin", e.Origin);
            cmd.Parameters.AddWithValue("$recordedAt", e.RecordedAt);
            cmd.Parameters.AddWithValue("$jointScore", e.JointScore is null ? DBNull.Value : e.JointScore.Value);
            if (cmd.ExecuteNonQuery() == 0) return false;

            using var idCmd = _conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            e.Id = Convert.ToInt64(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            return true;
        }
    }

    /// <summary>回填绩效；已评估过的不再覆盖。</summary>
    public void MarkOutcome(long id, SignalOutcome outcome)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE signals SET
                    outcome_status = $status, outcome_ret = $ret, outcome_excess = $excess,
                    outcome_mfe = $mfe, outcome_mae = $mae, outcome_stop_hit = $stopHit,
                    outcome_net_positive = $netPositive, outcome_evaluated_at = $evaluatedAt
                WHERE id = $id AND outcome_status IS NULL;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$status", outcome.Status);
            cmd.Parameters.AddWithValue("$ret", ToText(outcome.Ret));
            cmd.Parameters.AddWithValue("$excess", ToText(outcome.Excess));
            cmd.Parameters.AddWithValue("$mfe", ToText(outcome.Mfe));
            cmd.Parameters.AddWithValue("$mae", ToText(outcome.Mae));
            cmd.Parameters.AddWithValue("$stopHit", outcome.StopHit is null ? DBNull.Value : outcome.StopHit.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$netPositive", outcome.NetPositive is null ? DBNull.Value : outcome.NetPositive.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$evaluatedAt", outcome.EvaluatedAt ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 把联合打分补写到已存在的行（仅当该行尚无打分时）。用途：改进特征后重跑历史回填，
    /// 用同一自然键定位老样本、补齐 `joint_score`，而不产生重复行、不覆盖绩效。
    /// </summary>
    /// <remarks>打分是派生物（由 K 线复算得出），规则升级后重跑回填会覆盖旧分；绩效列不受影响。</remarks>
    public void FillJointScore(
        MarketKind market, TradingPair pair, string interval, string source, string side,
        long time, bool confirmed, string origin, int score)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE signals SET joint_score = $score
                WHERE market = $market AND base_asset = $base AND quote_asset = $quote AND interval = $interval
                  AND source = $source AND side = $side AND signal_time = $time AND is_confirmed = $confirmed
                  AND origin = $origin;
                """;
            cmd.Parameters.AddWithValue("$market", MarketKey(market));
            cmd.Parameters.AddWithValue("$base", pair.BaseAsset);
            cmd.Parameters.AddWithValue("$quote", pair.QuoteAsset);
            cmd.Parameters.AddWithValue("$interval", interval);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$side", side);
            cmd.Parameters.AddWithValue("$time", time);
            cmd.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
            cmd.Parameters.AddWithValue("$origin", origin);
            cmd.Parameters.AddWithValue("$score", score);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>全量快照（统计端点用；SQL 侧不做数值聚合，一律取回后按 decimal 计算）。</summary>
    public IReadOnlyList<SignalEntry> Snapshot()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = SelectPrefix + " ORDER BY signal_time;";
            return ReadAll(cmd);
        }
    }

    /// <summary>
    /// 取「持有期已满、尚未评估」的信号：按周期分档换算截止时间（各周期一根K线时长不同），
    /// 每档最多取 <paramref name="perIntervalLimit"/> 条，优先最旧的。
    /// </summary>
    public IReadOnlyList<SignalEntry> PendingOutcomes(long nowUnix, int horizonBars, int perIntervalLimit)
    {
        lock (_sync)
        {
            var intervals = new List<string>();
            using (var distinct = _conn.CreateCommand())
            {
                distinct.CommandText = "SELECT DISTINCT interval FROM signals WHERE outcome_status IS NULL;";
                using var reader = distinct.ExecuteReader();
                while (reader.Read()) intervals.Add(reader.GetString(0));
            }

            var pending = new List<SignalEntry>();
            foreach (var interval in intervals)
            {
                // 周期白名单外的行无法换算持有期，保持待评估并发出告警（正常写入路径已校验周期）
                if (!MarketIntervals.IsValid(interval))
                {
                    _logger.LogWarning("台账存在无法识别的周期 {Interval}，其待评估信号本轮跳过", interval);
                    continue;
                }
                var cutoff = nowUnix - MarketIntervals.IntervalSeconds(interval) * horizonBars;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = SelectPrefix + """
                     WHERE outcome_status IS NULL AND interval = $interval AND signal_time <= $cutoff
                     ORDER BY signal_time LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$interval", interval);
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                cmd.Parameters.AddWithValue("$limit", perIntervalLimit);
                // 历史数据里可能有锚定币行（规则上线前入库）：排除，避免无意义的K线拉取与统计污染
                pending.AddRange(ReadAll(cmd).Where(e => !e.Pair.IsPegged));
            }
            return pending;
        }
    }

    public void Dispose() => _conn.Dispose();

    // ---- 内部：建表 / 读写映射 / 旧格式导入 ----

    private const string SelectPrefix = """
        SELECT id, market, base_asset, quote_asset, interval, source, kind, side, signal_time, is_confirmed,
               price, stop_price, reference_price, note, confluence, adx, atr_pct,
               bandwidth_pct, origin, recorded_at,
               outcome_status, outcome_ret, outcome_excess, outcome_mfe, outcome_mae,
               outcome_stop_hit, outcome_net_positive, outcome_evaluated_at,
               joint_score
        FROM signals
        """;

    private void CreateSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS signals (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                joint_score          INTEGER,
                market               TEXT    NOT NULL,
                base_asset           TEXT    NOT NULL,
                quote_asset          TEXT    NOT NULL,
                interval             TEXT    NOT NULL,
                source               TEXT    NOT NULL,
                kind                 TEXT,
                side                 TEXT    NOT NULL,
                signal_time          INTEGER NOT NULL,
                is_confirmed         INTEGER NOT NULL CHECK (is_confirmed IN (0, 1)),
                price                TEXT    NOT NULL,
                stop_price           TEXT,
                reference_price      TEXT,
                note                 TEXT,
                confluence           TEXT,
                adx                  TEXT,
                atr_pct              TEXT,
                bandwidth_pct        TEXT,
                origin               TEXT    NOT NULL,
                recorded_at          INTEGER NOT NULL,
                outcome_status       TEXT,
                outcome_ret          TEXT,
                outcome_excess       TEXT,
                outcome_mfe          TEXT,
                outcome_mae          TEXT,
                outcome_stop_hit     INTEGER,
                outcome_net_positive INTEGER,
                outcome_evaluated_at INTEGER,
                UNIQUE (market, base_asset, quote_asset, interval, source, side, signal_time, is_confirmed, origin)
            );
            CREATE INDEX IF NOT EXISTS ix_signals_pending ON signals (interval, signal_time) WHERE outcome_status IS NULL;
            CREATE INDEX IF NOT EXISTS ix_signals_recorded ON signals (market, recorded_at);
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn("reference_price", "TEXT");
        EnsureColumn("kind", "TEXT");
        EnsureColumn("joint_score", "INTEGER");
        BackfillKindFromNote();
    }

    /// <summary>
    /// 补全历史行的类别列：缠论信号的说明文本固定以 "[类别] " 开头（如 "[3买] MACD 面积背驰…"），
    /// 早期入库的行没有独立类别列，按该前缀回填一次（幂等；非缠论来源不填）。
    /// </summary>
    private void BackfillKindFromNote()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE signals SET kind = substr(note, 2, instr(note, ']') - 2)
            WHERE kind IS NULL AND source = '缠论' AND note LIKE '[%]%';
            """;
        var updated = cmd.ExecuteNonQuery();
        if (updated > 0) _logger.LogInformation("台账按说明文本回填类别列 {Count} 行", updated);
    }

    /// <summary>增量补列：已存在的库（旧版本建表）缺少新增列时补上，避免要求用户删库重建。</summary>
    private void EnsureColumn(string name, string type)
    {
        using var check = _conn.CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('signals') WHERE name = $name;";
        check.Parameters.AddWithValue("$name", name);
        if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE signals ADD COLUMN {name} {type};";
        alter.ExecuteNonQuery();
        _logger.LogInformation("台账补齐新增列 {Column} {Type}", name, type);
    }

    private static IReadOnlyList<SignalEntry> ReadAll(SqliteCommand cmd)
    {
        var list = new List<SignalEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = new SignalEntry
            {
                Id = reader.GetInt64(0),
                Market = MarketKindExtensions.TryParse(reader.GetString(1), out var market) ? market : MarketKind.Spot,
                Pair = TradingPair.From(reader.GetString(2), reader.GetString(3)),
                Interval = reader.GetString(4),
                Source = reader.GetString(5),
                Kind = Text(reader, 6),
                Side = reader.GetString(7),
                Time = reader.GetInt64(8),
                IsConfirmed = reader.GetInt32(9) == 1,
                Price = FromText(reader.GetString(10)) ?? 0m,
                StopPrice = FromText(Text(reader, 11)),
                ReferencePrice = FromText(Text(reader, 12)),
                Note = Text(reader, 13),
                Confluence = Text(reader, 14),
                Adx = FromText(Text(reader, 15)),
                AtrPct = FromText(Text(reader, 16)),
                BandwidthPct = FromText(Text(reader, 17)),
                Origin = reader.GetString(18),
                RecordedAt = reader.GetInt64(19),
                JointScore = reader.IsDBNull(28) ? null : reader.GetInt32(28),
            };
            if (!reader.IsDBNull(21))
            {
                entry.Outcome = new SignalOutcome
                {
                    Status = reader.GetString(20),
                    Ret = FromText(Text(reader, 21)),
                    Excess = FromText(Text(reader, 22)),
                    Mfe = FromText(Text(reader, 23)),
                    Mae = FromText(Text(reader, 24)),
                    StopHit = reader.IsDBNull(25) ? null : reader.GetInt32(25) == 1,
                    NetPositive = reader.IsDBNull(26) ? null : reader.GetInt32(26) == 1,
                    EvaluatedAt = reader.IsDBNull(27) ? null : reader.GetInt32(27),
                };
            }
            list.Add(entry);
        }
        return list;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>decimal → TEXT：与 JSON 输出同一口径（八位小数、定点、无尾随零）。</summary>
    private static string ToText(decimal value) => DecimalJsonConverter.Format(value);

    private static object ToText(decimal? value) => value is null ? DBNull.Value : DecimalJsonConverter.Format(value.Value);

    private static decimal? FromText(string? text) =>
        string.IsNullOrEmpty(text) ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    private static string MarketKey(MarketKind market) => market.ToString().ToLowerInvariant();

    /// <summary>
    /// 一次性导入旧格式（JSONL 台账，标的为连写字符串、含 key 列）。
    /// 仅在 signals 表为空且旧文件存在时执行；旧文件保留为备份，之后不再写入。
    /// 标的按连写规则切成交易币/计价币（<see cref="TradingPair.TryParse"/>）——旧记录本就出自本系统，
    /// 其标的当初由 exchangeInfo 解析而来，连写切分足以还原；切不开的行跳过并计入日志。
    /// </summary>
    private void ImportLegacyJsonlIfEmpty()
    {
        if (Count > 0 || !File.Exists(_legacyJsonlPath)) return;

        var imported = 0;
        var skipped = 0;
        var pairs = new SortedSet<string>(StringComparer.Ordinal);
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        foreach (var line in File.ReadLines(_legacyJsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            LegacyEntry? legacy;
            try
            {
                legacy = JsonSerializer.Deserialize<LegacyEntry>(line, jsonOpts);
            }
            catch (JsonException)
            {
                skipped++;
                continue;
            }
            if (legacy is null
                || !MarketKindExtensions.TryParse(legacy.Market, out var market)
                || !TradingPair.TryParse(legacy.Symbol, out var pair))
            {
                skipped++;
                continue;
            }

            var entry = new SignalEntry
            {
                Market = market,
                Pair = pair,
                Interval = legacy.Interval,
                Source = legacy.Source,
                Kind = legacy.Kind,
                Side = legacy.Side,
                Time = legacy.Time,
                Price = legacy.Price,
                Note = legacy.Note,
                StopPrice = legacy.StopPrice,
                ReferencePrice = legacy.ReferencePrice,
                Confluence = legacy.Confluence,
                IsConfirmed = legacy.IsConfirmed,
                Adx = legacy.Adx,
                AtrPct = legacy.AtrPct,
                BandwidthPct = legacy.BandwidthPct,
                RecordedAt = legacy.RecordedAt,
                Origin = legacy.Origin ?? "live",
                Outcome = legacy.Outcome,
            };
            if (!TryRecord(entry))
            {
                skipped++;   // 自然键重复
                continue;
            }
            if (entry.Outcome is not null) MarkOutcome(entry.Id, entry.Outcome);
            pairs.Add(pair.Display);
            imported++;
        }

        _logger.LogInformation(
            "信号台账：已从旧格式 signals.jsonl 导入 {Imported} 条（跳过 {Skipped} 条），涉及 {Pairs} 个交易对；" +
            "旧文件保留为备份，此后以 signals.db 为准",
            imported, skipped, pairs.Count);
        _logger.LogInformation("导入的交易对：{Pairs}", string.Join(", ", pairs));
    }

    /// <summary>旧 JSONL 台账的行结构（仅用于一次性导入，勿在新代码中使用）。</summary>
    private sealed class LegacyEntry
    {
        [JsonPropertyName("market")] public string Market { get; set; } = "";
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
        [JsonPropertyName("interval")] public string Interval { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("side")] public string Side { get; set; } = "";
        [JsonPropertyName("time")] public long Time { get; set; }
        [JsonPropertyName("price")] public decimal Price { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("stopPrice")] public decimal? StopPrice { get; set; }
        [JsonPropertyName("referencePrice")] public decimal? ReferencePrice { get; set; }
        [JsonPropertyName("confluence")] public string? Confluence { get; set; }
        [JsonPropertyName("isConfirmed")] public bool IsConfirmed { get; set; }
        [JsonPropertyName("adx")] public decimal? Adx { get; set; }
        [JsonPropertyName("atrPct")] public decimal? AtrPct { get; set; }
        [JsonPropertyName("bandwidthPct")] public decimal? BandwidthPct { get; set; }
        [JsonPropertyName("recordedAt")] public long RecordedAt { get; set; }
        [JsonPropertyName("origin")] public string? Origin { get; set; }
        [JsonPropertyName("outcome")] public SignalOutcome? Outcome { get; set; }
    }
}

/// <summary>单来源信号的历史绩效汇总。</summary>
public sealed record SignalSourceStats(
    string Source,
    int N,
    int NEpisodes,
    decimal WinRate,
    decimal AvgReturn,
    decimal AvgExcess,
    decimal NetPositiveRate,
    decimal StopHitRate,
    string Grade,
    /// <summary>分级依据说明（不达标时给出具体原因，便于前端展示与自查）。</summary>
    string GradeReason,
    /// <summary>集中度：样本最多的单一标的占比（0~1），用于识别"靠单个标的的行情撑起统计"。</summary>
    decimal TopSymbolShare,
    /// <summary>其中来自历史回填的样本数（事后生成，参考价值低于在线样本）。</summary>
    int NBackfill);


/// <summary>按市场状态/共振状态分组的绩效（用于判断信号的状态依赖性）。</summary>
public sealed record SignalBucketStats(
    string Label,
    int N,
    decimal WinRate,
    decimal AvgExcess,
    decimal NetPositiveRate);

public sealed record SignalStatsResponse(
    /// <summary>统计窗口口径：按**记录时间**（RecorderAt）筛选，而非信号K线时间——
    /// 回填样本的 recordedAt 是回填运行时刻，因此"近N天"对回填样本等价于"全部回填样本"。</summary>
    string WindowBasis,
    /// <summary>窗口内的独立波次数（当前生效的实盘判据样本量）。</summary>
    int WindowEpisodes,
    /// <summary>其中**实时落库**的独立波次数（事实计数；"可实盘"判据已废弃，见 SignalQualityRules 注释）。</summary>
    int RealtimeEpisodes,
    int TotalEvaluated,
    SignalSourceStats? Overall,
    IReadOnlyList<SignalSourceStats> BySource,
    IReadOnlyList<SignalBucketStats> ByRegime,
    /// <summary>按级别共振标签分组。已移除"顺/逆大势"分组：该维度在线恒为空（纯缠论后不再计算 trendAligned），
    /// 仅有回填样本有值，两批样本不可比（2026-09-30 审查，见 PLAN §0.20）。</summary>
    IReadOnlyList<SignalBucketStats> ByConfluence);
