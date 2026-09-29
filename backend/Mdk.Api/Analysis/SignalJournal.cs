using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mdk.Api.Analysis;

/// <summary>信号的事后绩效（由 SignalOutcomeService 在持有期满后回填）。</summary>
public sealed class SignalOutcome
{
    /// <summary>ok=已评估；expired=信号过旧超出可用K线范围，无法评估。</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "ok";
    [JsonPropertyName("ret")] public double? Ret { get; set; }
    [JsonPropertyName("excess")] public double? Excess { get; set; }
    [JsonPropertyName("mfe")] public double? Mfe { get; set; }
    [JsonPropertyName("mae")] public double? Mae { get; set; }
    [JsonPropertyName("stopHit")] public bool? StopHit { get; set; }
    [JsonPropertyName("netPositive")] public bool? NetPositive { get; set; }
    [JsonPropertyName("evaluatedAt")] public long? EvaluatedAt { get; set; }
}

/// <summary>一条已记录信号的完整档案（JSONL 持久化）。</summary>
public sealed class SignalJournalEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("market")] public string Market { get; set; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("interval")] public string Interval { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("time")] public long Time { get; set; }
    [JsonPropertyName("price")] public double Price { get; set; }
    /// <summary>信号说明原文（含位点价位/触碰次数等，便于事后审计）。</summary>
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("stopPrice")] public double? StopPrice { get; set; }
    [JsonPropertyName("trendAligned")] public bool? TrendAligned { get; set; }
    /// <summary>级别共振标签（aligned/counter/none，仅缠论信号）。</summary>
    [JsonPropertyName("confluence")] public string? Confluence { get; set; }
    [JsonPropertyName("isConfirmed")] public bool IsConfirmed { get; set; }
    /// <summary>信号发生时的市场状态（用于按状态分组统计，如 ADX≥25 的趋势市 vs 震荡市）。</summary>
    [JsonPropertyName("adx")] public double? Adx { get; set; }
    [JsonPropertyName("atrPct")] public double? AtrPct { get; set; }
    [JsonPropertyName("bandwidthPct")] public double? BandwidthPct { get; set; }
    [JsonPropertyName("recordedAt")] public long RecordedAt { get; set; }
    /// <summary>来源：live=部署后在线记录；backfill=历史回填（事后一次性生成，存在事后挑选风险，统计时需区分）。</summary>
    [JsonPropertyName("origin")] public string Origin { get; set; } = "live";
    [JsonPropertyName("outcome")] public SignalOutcome? Outcome { get; set; }
}

/// <summary>
/// 信号落库：追加式 JSONL（data/signals.jsonl），键去重（同市场/币种/周期/来源/方向/时间/确认级别只记一次）。
/// 目的：让「置信度」从启发式分数变成可验证的历史统计——每个信号在持有期满后由后台服务回填绩效。
/// </summary>
public sealed class SignalJournal
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly Lock _sync = new();
    private readonly string _filePath;
    private readonly List<SignalJournalEntry> _entries = [];
    private readonly HashSet<string> _keys = [];

    public SignalJournal(IHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "signals.jsonl");
        if (File.Exists(_filePath))
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<SignalJournalEntry>(line, JsonOpts);
                    if (entry is null || _keys.Contains(entry.Key)) continue;
                    _entries.Add(entry);
                    _keys.Add(entry.Key);
                }
                catch (JsonException)
                {
                    // 跳过损坏行
                }
            }
        }
    }

    public int Count { get { lock (_sync) return _entries.Count; } }

    /// <summary>记录一条信号；重复键返回 false。</summary>
    public bool TryRecord(SignalJournalEntry entry)
    {
        lock (_sync)
        {
            if (!_keys.Add(entry.Key)) return false;
            _entries.Add(entry);
            File.AppendAllText(_filePath, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine);
            return true;
        }
    }

    /// <summary>回填绩效并重写文件（低频操作，全量重写可接受）。</summary>
    public void MarkOutcome(string key, SignalOutcome outcome)
    {
        lock (_sync)
        {
            var entry = _entries.FirstOrDefault(e => e.Key == key);
            if (entry is null || entry.Outcome is not null) return;
            entry.Outcome = outcome;
            var lines = _entries.Select(e => JsonSerializer.Serialize(e, JsonOpts));
            File.WriteAllLines(_filePath, lines);
        }
    }

    public IReadOnlyList<SignalJournalEntry> Snapshot()
    {
        lock (_sync) return [.. _entries];
    }
}

/// <summary>单来源信号的历史绩效汇总。</summary>
public sealed record SignalSourceStats(
    string Source,
    int N,
    int NEpisodes,
    double WinRate,
    double AvgReturn,
    double AvgExcess,
    double NetPositiveRate,
    double StopHitRate,
    string Grade,
    /// <summary>分级依据说明（不达标时给出具体原因，便于前端展示与自查）。</summary>
    string GradeReason,
    /// <summary>集中度：样本最多的单一标的占比（0~1），用于识别"靠单个标的的行情撑起统计"。</summary>
    double TopSymbolShare,
    /// <summary>其中来自历史回填的样本数（事后生成，参考价值低于在线样本）。</summary>
    int NBackfill);


/// <summary>按市场状态/共振状态分组的绩效（用于判断信号的状态依赖性）。</summary>
public sealed record SignalBucketStats(
    string Label,
    int N,
    double WinRate,
    double AvgExcess,
    double NetPositiveRate);

public sealed record SignalStatsResponse(
    int TotalEvaluated,
    SignalSourceStats? Overall,
    IReadOnlyList<SignalSourceStats> BySource,
    IReadOnlyList<SignalBucketStats> ByRegime,
    IReadOnlyList<SignalBucketStats> ByAlignment,
    /// <summary>按级别共振标签分组（仅缠论信号有标签）。</summary>
    IReadOnlyList<SignalBucketStats> ByConfluence);
