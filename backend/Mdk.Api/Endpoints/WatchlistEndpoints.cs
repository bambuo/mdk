using Mdk.Api.Analysis;
using Mdk.Api.Binance;
using Mdk.Api.Domain;

namespace Mdk.Api.Endpoints;

// 监控列表（用户指定、持久化）与监控页所需的数据。
internal static class WatchlistEndpoints
{
    public sealed record AddRequest(string? Market, string? Symbol, string[]? Intervals);

    public sealed record WatchlistSignalView(
        string Market, string Symbol, string BaseAsset, string QuoteAsset,
        string Interval, string Source, string? Kind, string Side, long Time, decimal Price,
        decimal? StopPrice, string? Note, string? Confluence, bool IsConfirmed, long RecordedAt,
        // 同语境（缠论类别 × 周期）的经验可信度；指标信号不评级（可信度仅对缠论结构定义）。
        EvidenceBucket? Evidence);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/watchlist", (WatchlistMonitorService monitor) => Results.Ok(monitor.BuildView()))
            .WithSummary("监控列表 + 各周期结构快照 + 跨周期共振");

        app.MapPost("/api/watchlist", async (
                AddRequest req, SymbolCatalog catalog, WatchlistMonitorService monitor,
                ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                var (market, pair, error) = await PairResolver.ResolveAsync(req.Symbol, req.Market, catalog, ct);
                if (pair is null) return Results.BadRequest(new { error });
                if (pair.Value.IsPegged)
                    return Results.BadRequest(new { error = $"{pair.Value.Display} 是锚定币（价格恒定），不构成分析标的" });
                var intervals = WatchlistStore.NormalizeIntervals(req.Intervals);
                if (intervals.Count == 0)
                    return Results.BadRequest(new { error = $"请至少指定一个有效周期：{string.Join(", ", MarketIntervals.All)}" });

                var item = monitor.Store.Upsert(market, pair.Value, intervals);
                // 立刻预热（不阻塞响应）：新增条目后监控页很快就有结构快照
                var logger = loggerFactory.CreateLogger("WatchlistEndpoints");
                _ = Task.Run(async () =>
                {
                    try { await monitor.AnalyzeItemAsync(item, CancellationToken.None); }
                    catch (Exception ex) { logger.LogWarning(ex, "监控条目预热失败 {Symbol}", item.Pair.Display); }
                }, CancellationToken.None);
                return Results.Ok(item);
            })
            .WithSummary("新增/更新监控交易对（market/symbol/intervals）");

        app.MapPost("/api/watchlist/toggle", async (
                string? market, string? symbol, SymbolCatalog catalog, WatchlistMonitorService monitor,
                CancellationToken ct) =>
            {
                var (marketKind, pair, error) = await PairResolver.ResolveAsync(symbol, market, catalog, ct);
                if (pair is null) return Results.BadRequest(new { error });
                var enabled = monitor.Store.Toggle(marketKind, pair.Value);
                return enabled is null
                    ? Results.NotFound(new { error = "监控列表中不存在该交易对" })
                    : Results.Ok(new { enabled });
            })
            .WithSummary("启用/停用监控交易对");

        app.MapDelete("/api/watchlist", async (
                string? market, string? symbol, SymbolCatalog catalog, WatchlistMonitorService monitor,
                CancellationToken ct) =>
            {
                var (marketKind, pair, error) = await PairResolver.ResolveAsync(symbol, market, catalog, ct);
                if (pair is null) return Results.BadRequest(new { error });
                return monitor.Store.Remove(marketKind, pair.Value)
                    ? Results.Ok(new { removed = true })
                    : Results.NotFound(new { error = "监控列表中不存在该交易对" });
            })
            .WithSummary("移除监控交易对");

        // SSE：监控列表内新入库的已确认缠论买卖点，实时推给前端（替代此前的 60s 前端轮询）。
        // 事件过滤器在 WatchlistSignalBroadcaster（来源/确认/origin/监控成员），此处只负责传输与心跳。
        app.MapGet("/api/watchlist/stream", async (
                HttpContext context, WatchlistSignalBroadcaster hub, CancellationToken requestAborted) =>
            {
                var id = hub.Subscribe(out var reader);
                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                // SSE 注释行作为心跳：保活并让代理不判空闲（30s 一次）
                using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(30));
                try
                {
                    await context.Response.Body.WriteAsync(": connected\n\n"u8.ToArray(), requestAborted);
                    await context.Response.Body.FlushAsync(requestAborted);

                    _ = Task.Run(async () =>
                    {
                        await foreach (var payload in reader.ReadAllAsync(requestAborted))
                        {
                            await context.Response.WriteAsync($"data: {payload}\n\n", requestAborted);
                            await context.Response.Body.FlushAsync(requestAborted);
                        }
                    }, requestAborted);

                    while (await heartbeat.WaitForNextTickAsync(requestAborted))
                    {
                        await context.Response.Body.WriteAsync(": ping\n\n"u8.ToArray(), requestAborted);
                        await context.Response.Body.FlushAsync(requestAborted);
                    }
                }
                catch (OperationCanceledException) { /* 客户端断开或停机，属正常退出 */ }
                finally
                {
                    hub.Unsubscribe(id);
                }
                return Results.Empty;
            })
            .WithSummary("SSE：监控列表内新入库的已确认缠论买卖点（实时推送，替代前端轮询）");

        app.MapGet("/api/watchlist/signals", (
                int? limit, WatchlistMonitorService monitor, SignalStore signals) =>
            {
                var take = Math.Clamp(limit ?? 50, 1, 200);
                var keys = monitor.Store.All().Select(x => (x.Market, x.Pair)).ToHashSet();
                var snapshot = signals.Snapshot();
                // 监控列表内的**已确认**信号，全部来源（缠论 + 指标），按时间倒序
                var rows = snapshot
                    .Where(e => e.IsConfirmed && keys.Contains((e.Market, e.Pair)))
                    .OrderByDescending(e => e.Time)
                    .Take(take)
                    .ToList();
                // 缠论行的可信度按 (类别 × 周期) 查同语境台账（与查看页同一口径），指标信号不评级
                var buckets = rows.Where(e => e.Source == "缠论")
                    .Select(e => (e.Market, e.Interval, e.Kind)).Distinct()
                    .ToDictionary(
                        key => key,
                        key => EvidenceRules.BuildFromStore(key.Kind, key.Interval, key.Market, snapshot));
                return Results.Ok(rows.Select(e => new WatchlistSignalView(
                    e.Market.ToString().ToLowerInvariant(), e.Pair.Symbol, e.Pair.BaseAsset, e.Pair.QuoteAsset,
                    e.Interval, e.Source, e.Kind, e.Side, e.Time, e.Price, e.StopPrice, e.Note, e.Confluence,
                    e.IsConfirmed, e.RecordedAt,
                    e.Source == "缠论" ? buckets[(e.Market, e.Interval, e.Kind)] : null)));
            })
            .WithSummary("监控列表内的最近信号（全部来源，缠论行带可信度）");
    }
}
