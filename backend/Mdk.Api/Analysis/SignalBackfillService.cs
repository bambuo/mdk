using Mdk.Api.Analysis.Chan;
using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Analysis;

/// <summary>历史回填配置（appsettings "Backfill" 节）。</summary>
public sealed class BackfillOptions
{
    public const string SectionName = "Backfill";

    /// <summary>是否在服务启动后自动回填一次。</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>启动后延迟多久开始（秒），避免与首屏请求竞争。</summary>
    public int StartupDelaySeconds { get; set; } = 40;

    /// <summary>回填天数（币安可提供更长历史，按需调整）。</summary>
    public int Days { get; set; } = 90;

    /// <summary>
    /// 回填的标的列表。默认留空（不用预置数组默认值）：
    /// .NET 配置绑定对集合是"追加"而非"替换"，预置默认值会导致实际条目 = 默认 + 配置（曾出现 5+5=10 的重复标的）。
    /// </summary>
    public string[] Symbols { get; set; } = [];

    /// <summary>回填的周期列表（4h 的次级别是 1h，1h 的次级别是 15m，数据量差异较大）；同上，默认留空。</summary>
    public string[] Intervals { get; set; } = [];
}

/// <summary>回填进度（供 /api/backfill/status 查询）。</summary>
public sealed record BackfillStatus(
    bool Running,
    string? Current,
    int Completed,
    int Total,
    long SignalsRecorded,
    long StartedAtUnix,
    long FinishedAtUnix,
    string? LastError);

/// <summary>
/// 历史回填服务：把币安历史K线沉淀为可评估的缠论样本。
/// 逐根复算（ChanBackfill.Replay），严格与在线同口径；记录的条目 Origin="backfill"，便于统计时区分。
/// </summary>
public sealed class SignalBackfillService(
    BinanceRestClient rest,
    SymbolCatalog catalog,
    SignalJournal journal,
    IOptions<BackfillOptions> backfillOptions,
    IOptions<ChanOptions> chanOptions,
    ILogger<SignalBackfillService> logger) : BackgroundService
{
    private readonly BackfillOptions _options = backfillOptions.Value;
    private readonly ChanOptions _chanOptions = chanOptions.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile BackfillStatus _status = new(false, null, 0, 0, 0, 0, 0, null);

    public BackfillStatus Status => _status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunOnStartup)
        {
            logger.LogInformation("历史回填已禁用（Backfill:RunOnStartup=false）");
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds)), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_options.Symbols.Length == 0 || _options.Intervals.Length == 0)
        {
            logger.LogInformation("历史回填跳过：未配置 Backfill:Symbols / Backfill:Intervals");
            return;
        }
        await RunAsync(_options.Days, _options.Symbols, _options.Intervals, stoppingToken);
    }

    /// <summary>执行一次回填（同一时刻只允许一个）。</summary>
    public async Task<BackfillStatus> RunAsync(int days, IReadOnlyList<string> symbols, IReadOnlyList<string> intervals, CancellationToken ct, bool subLevelConfirm = true)
    {
        if (!await _gate.WaitAsync(0, ct))
            return _status;   // 已有回填在跑

        var total = symbols.Count * intervals.Count;
        var completed = 0;
        long recorded = 0;
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _status = new BackfillStatus(true, null, 0, total, 0, startedAt, 0, null);
        try
        {
            logger.LogInformation("历史回填开始：{Days} 天 × {Symbols} 标的 × {Intervals} 周期",
                days, symbols.Count, intervals.Count);

            foreach (var interval in intervals)
            {
                if (!MarketIntervals.IsValid(interval)) continue;
                var subInterval = _chanOptions.RequireSubLevelConfirm ? MarketIntervals.LowerInterval(interval) : null;
                var htfInterval = MarketIntervals.HigherInterval(interval);

                foreach (var symbolRaw in symbols)
                {
                    ct.ThrowIfCancellationRequested();
                    var pair = await catalog.ResolveAsync(MarketKind.Spot, symbolRaw, ct);
                    if (pair is null)
                    {
                        logger.LogWarning("回填跳过未知标的 {Symbol}", symbolRaw);
                        completed++;
                        continue;
                    }
                    _status = _status with { Current = $"{pair.Value.Symbol} {interval}", Completed = completed };
                    try
                    {
                        recorded += await SeedOneAsync(pair.Value, interval, subInterval, htfInterval, days, ct, subLevelConfirm);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "回填失败 {Symbol} {Interval}", pair.Value.Symbol, interval);
                        _status = _status with { LastError = $"{pair.Value.Symbol} {interval}: {ex.Message}" };
                    }
                    completed++;
                    _status = _status with { Completed = completed, SignalsRecorded = recorded };
                }
            }

            _status = _status with
            {
                Running = false,
                Current = null,
                Completed = completed,
                SignalsRecorded = recorded,
                FinishedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            logger.LogInformation("历史回填完成：新增 {Recorded} 条缠论样本", recorded);
        }
        finally
        {
            _gate.Release();
        }
        return _status;
    }

    private async Task<long> SeedOneAsync(
        TradingPair pair, string interval, string? subInterval, string? htfInterval, int days, CancellationToken ct,
        bool subLevelConfirm = true)
    {
        var barSeconds = MarketIntervals.IntervalSeconds(interval);
        var windowBars = Math.Max(120, _chanOptions.EffectiveAnalysisBars);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var toSec = now;
        // 需要 windowBars 根预热历史，才能从 (now - days) 起逐根复算
        var fromSec = now - days * 86400L - (long)(windowBars * barSeconds * 1.05);

        var main = await rest.GetKlinesRangeAsync(MarketKind.Spot, pair, interval, fromSec, toSec, ct);
        if (main.Length < windowBars + 10)
        {
            logger.LogInformation("回填跳过 {Symbol} {Interval}：历史不足（{Count} 根）", pair.Symbol, interval, main.Length);
            return 0;
        }

        IReadOnlyList<Models.Candle>? sub = null;
        if (subInterval is not null)
        {
            var subBars = MarketIntervals.IntervalSeconds(subInterval);
            sub = await rest.GetKlinesRangeAsync(MarketKind.Spot, pair, subInterval, fromSec - 300 * subBars, toSec, ct);
        }
        IReadOnlyList<Models.Candle>? htf = null;
        if (htfInterval is not null)
        {
            // 高周期同样要"逐根复算"，因此需要 windowBars 根自身预热（此前只留 60 根余量，
            // 导致高周期几乎产不出信号、共振标签形同虚设 —— 2026-09-29 实测发现）
            var htfBars = MarketIntervals.IntervalSeconds(htfInterval);
            var htfWarmup = (long)(windowBars * htfBars * 1.05);
            htf = await rest.GetKlinesRangeAsync(MarketKind.Spot, pair, htfInterval, fromSec - htfWarmup, toSec, ct);
        }

        var seedFrom = now - days * 86400L;
        // A/B 对照：可关闭次级别确认（Origin 与键后缀区分，互不覆盖）
        var options = subLevelConfirm ? _chanOptions : _chanOptions.Clone(requireSubLevelConfirm: false);
        var origin = subLevelConfirm ? "backfill" : "backfill-nosub";
        var keySuffix = subLevelConfirm ? "|bf" : "|bfn";
        var signals = ChanBackfill.Replay(main, sub, subInterval, htf, htfInterval, options, seedFrom, toSec);

        var inserted = 0L;
        var recordedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var s in signals)
        {
            var key = $"spot|{pair.Symbol}|{interval}|缠论|{s.Side}|{s.Time}|1{keySuffix}";
            if (journal.TryRecord(new SignalJournalEntry
            {
                Key = key,
                Market = "spot",
                Symbol = pair.Symbol,
                Interval = interval,
                Source = "缠论",
                Side = s.Side,
                Time = s.Time,
                Price = s.Price,
                Note = $"[{s.Kind}] {s.Note}",
                StopPrice = s.StopPrice,
                TrendAligned = s.TrendAligned,
                Confluence = s.Confluence,
                IsConfirmed = true,
                Adx = s.Adx,
                AtrPct = s.AtrPct,
                BandwidthPct = s.BandwidthPct,
                RecordedAt = recordedAt,
                Origin = origin,
            }))
            {
                inserted++;
            }
        }

        logger.LogInformation("回填 {Symbol} {Interval}（次级别确认={Sub}）：窗口 {Bars} 根，产出信号 {Total} 个（新增 {Inserted} 条）",
            pair.Symbol, interval, subLevelConfirm, main.Length, signals.Count, inserted);
        return inserted;
    }
}
