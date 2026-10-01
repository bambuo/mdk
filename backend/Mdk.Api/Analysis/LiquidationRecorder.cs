using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Mdk.Api.Analysis;

/// <summary>
/// 强平（清算）快照采集：订阅币安全市场强平流 <c>!forceOrder@arr</c>，按 (标的, 分钟) 聚合并落库。
///
/// 为什么采集而不是回填：币安已下架历史强平打包文件（2026-10 实测 vision 列表里已无 liquidationSnapshot），
/// 历史只能从订阅那一刻开始积累。
///
/// 完整性缺陷（写在这里以免被误用）：该流**只推每秒每标的最大一笔**，故采集量系统性少于真实清算量；
/// 用途是相对比较（哪一侧在挨打、哪个时段更凶），不是清算总额。只记录**监控列表内**的标的。
/// </summary>
public sealed class LiquidationRecorder(
    WatchlistStore watchlist,
    LiquidationStore store,
    ILogger<LiquidationRecorder> logger) : BackgroundService
{
    private const string Url = "wss://fstream.binance.com/market/stream?streams=!forceOrder@arr";

    /// <summary>数据保留天数（更早的分钟级明细清理掉）。</summary>
    private const int RetentionDays = 30;

    private readonly ConcurrentDictionary<string, Accumulator> _pending = new();
    private long _received;
    private long _recorded;

    /// <summary>汇总（供分析接口使用）：自 hours 小时前的分钟起。</summary>
    public LiquidationSummary Summary(string symbol, int hours)
    {
        var fromMinute = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hours * 3600L) / 60;
        return store.Summary(symbol, fromMinute);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flush = FlushLoopAsync(stoppingToken);
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                await ws.ConnectAsync(new Uri(Url), stoppingToken);
                logger.LogInformation("清算流已连接（全市场强平快照；只记录监控列表内标的）");
                backoff = TimeSpan.FromSeconds(1);

                var buffer = new byte[16 * 1024];
                while (!stoppingToken.IsCancellationRequested)
                {
                    var message = await ReceiveAsync(ws, buffer, stoppingToken);
                    if (message is null) break;
                    Accept(message);
                }

                logger.LogWarning("清算流被远端关闭，准备重连");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "清算流异常，{Delay} 后重连", backoff);
            }

            try
            {
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await flush;
    }

    private void Accept(string message)
    {
        if (LiquidationMessage.TryParse(message) is not { } order) return;
        Interlocked.Increment(ref _received);
        var minute = order.TimeMs / 60_000;
        var accumulator = _pending.GetOrAdd($"{order.Symbol}|{minute}", _ => new Accumulator(order.Symbol, minute));
        accumulator.Add(order.Side, order.Qty, order.Price);
    }

    /// <summary>每 30 秒把**已结束的分钟**落库（当前分钟留在内存继续累加），并定期清理过期数据。</summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var lastPurgeMinute = 0L;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var currentMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                var watched = watchlist.All().Where(i => i.Enabled).Select(i => i.Pair.Symbol)
                    .ToHashSet(StringComparer.Ordinal);

                var buckets = new List<LiquidationBucket>();
                foreach (var (key, accumulator) in _pending)
                {
                    if (accumulator.Minute >= currentMinute) continue;         // 当前分钟继续累加
                    if (!_pending.TryRemove(key, out var taken)) continue;
                    if (!watched.Contains(taken.Symbol)) continue;             // 只留监控列表内的标的
                    buckets.Add(taken.ToBucket());
                    Interlocked.Add(ref _recorded, taken.Count);
                }

                store.Add(buckets);

                if (currentMinute / 1440 != lastPurgeMinute / 1440)
                {
                    lastPurgeMinute = currentMinute;
                    var removed = store.Purge(currentMinute - (long)RetentionDays * 1440);
                    if (removed > 0) logger.LogInformation("清算数据清理：删除 {Rows} 行（保留 {Days} 天）", removed, RetentionDays);
                }

                if (buckets.Count > 0)
                {
                    logger.LogInformation("清算采集：收到 {Received} 条（落库 {Recorded} 条，本批 {Buckets} 个标的分钟）",
                        Interlocked.Read(ref _received), Interlocked.Read(ref _recorded), buckets.Count);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "清算数据落库失败（下一轮重试）");
            }
        }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return builder.ToString();
        }
    }

    /// <summary>一个 (标的, 分钟) 的累加器（只在采集线程与落库线程间交接，自身不加锁）。</summary>
    private sealed class Accumulator(string symbol, long minute)
    {
        private int _count;
        private decimal _buyQty;
        private decimal _sellQty;
        private decimal _buyNotional;
        private decimal _sellNotional;
        private decimal _maxNotional;

        public string Symbol { get; } = symbol;

        public long Minute { get; } = minute;

        public int Count => _count;

        /// <summary>SELL = 多头被强平（卖出平多）；BUY = 空头被强平。</summary>
        public void Add(string side, decimal qty, decimal price)
        {
            var notional = qty * price;
            _count++;
            _maxNotional = Math.Max(_maxNotional, notional);
            if (side == "SELL")
            {
                _sellQty += qty;
                _sellNotional += notional;
            }
            else
            {
                _buyQty += qty;
                _buyNotional += notional;
            }
        }

        public LiquidationBucket ToBucket() =>
            new(Symbol, Minute, _count, _buyQty, _sellQty, _buyNotional, _sellNotional, _maxNotional);
    }
}
