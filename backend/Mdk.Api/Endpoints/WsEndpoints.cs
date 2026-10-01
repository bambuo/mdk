using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Mdk.Api.Analysis;
using Mdk.Api.Binance;
using Mdk.Api.Domain;
using Mdk.Api.Models;

namespace Mdk.Api.Endpoints;

internal static class WsEndpoints
{
    /// <summary>WS 消息统一序列化配置（camelCase + TradingPair 连写 + 枚举小写 + decimal 去伪精度）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new TradingPairJsonConverter(), new DecimalJsonConverter(), new NullableDecimalJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/ws", async (
                HttpContext context, string? market, string? symbol, string? interval,
                SymbolCatalog catalog, KlineStreamService streams, AnalysisService analysisService,
                ILoggerFactory loggerFactory) =>
            {
                if (!MarketKindExtensions.TryParse(market, out var marketKind))
                    return Results.Json(new { error = $"不支持的市场 “{market}”，可用：spot, futures" }, statusCode: 400);
                var (_, pair, error) = await PairResolver.ResolveAsync(symbol, market, catalog, context.RequestAborted);
                if (pair is null)
                    return Results.Json(new { error }, statusCode: 400);
                if (interval is null || !MarketIntervals.IsValid(interval))
                    return Results.Json(new
                    {
                        error = $"不支持的周期 “{interval}”，可用：{string.Join(", ", MarketIntervals.All)}"
                    }, statusCode: 400);
                if (!context.WebSockets.IsWebSocketRequest)
                    return Results.Json(new { error = "该端点需要 WebSocket 连接" }, statusCode: 400);

                var socket = await context.WebSockets.AcceptWebSocketAsync();
                var handler = new KlineSocketHandler(
                    marketKind, pair.Value, interval, streams, analysisService,
                    loggerFactory.CreateLogger("KlineSocket"));
                await handler.RunAsync(socket, context.RequestAborted);
                return Results.Empty;
            })
            .WithSummary("实时K线与分析结果推送（?market=&symbol=&interval=）");
    }
}

internal sealed record WsEnvelope(string Type, object? Data);

/// <summary>
/// 单条浏览器 WS 连接的会话：
/// 上游 kline 增量 → 前端；K线收盘或 20 秒节流触发 → 重算 analysis → 前端。
/// 所有出站消息经单一队列串行发送（WebSocket 不允许并发写）。
/// </summary>
internal sealed class KlineSocketHandler(
    MarketKind market,
    TradingPair pair,
    string interval,
    KlineStreamService streams,
    AnalysisService analysisService,
    ILogger logger)
{
    private static readonly TimeSpan AnalysisRefreshInterval = TimeSpan.FromSeconds(20);
    private const int AnalysisKlineLimit = 500;

    private readonly Channel<KlineUpdate> _incoming =
        Channel.CreateUnbounded<KlineUpdate>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<string> _outbound =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<bool> _analysisTriggers =
        Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });

    public async Task RunAsync(WebSocket socket, CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = linked.Token;

        var subscription = streams.Subscribe(market, pair, interval, update =>
        {
            if (update.Interval == interval) _incoming.Writer.TryWrite(update);
        });

        var klineLoop = Task.Run(() => KlineLoopAsync(ct), ct);
        var analysisLoop = Task.Run(() => AnalysisLoopAsync(ct), ct);
        var periodic = new PeriodicTimer(AnalysisRefreshInterval);
        var periodicLoop = Task.Run(async () =>
        {
            while (await periodic.WaitForNextTickAsync(ct))
                _analysisTriggers.Writer.TryWrite(false);
        });
        _analysisTriggers.Writer.TryWrite(true); // 连接后先推一次完整分析

        try
        {
            var reader = _outbound.Reader;
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var message))
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或服务关闭
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning("WS 发送失败（客户端断开?）: {Message}", ex.Message);
        }
        finally
        {
            await linked.CancelAsync();
            subscription.Dispose();
            periodic.Dispose();
            _incoming.Writer.TryComplete();
            _analysisTriggers.Writer.TryComplete();
            try
            {
                await Task.WhenAll(klineLoop, analysisLoop, periodicLoop);
            }
            catch
            {
                // 循环被取消属预期
            }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closing",
                        CancellationToken.None);
                }
                catch
                {
                    // 客户端已断开
                }
            }
        }
    }

    private async Task KlineLoopAsync(CancellationToken ct)
    {
        await foreach (var update in _incoming.Reader.ReadAllAsync(ct))
        {
            _outbound.Writer.TryWrite(
                JsonSerializer.Serialize(new WsEnvelope("kline", update), WsEndpoints.JsonOptions));
            if (update.IsFinal) _analysisTriggers.Writer.TryWrite(true);
        }
    }

    private async Task AnalysisLoopAsync(CancellationToken ct)
    {
        await foreach (var trigger in _analysisTriggers.Reader.ReadAllAsync(ct))
        {
            if (!trigger)
            {
                // 盘中节流触发：只保留最新
                while (_analysisTriggers.Reader.TryRead(out _))
                {
                }
            }

            try
            {
                var result = await analysisService.AnalyzeAsync(market, pair, interval, AnalysisKlineLimit, ct);
                _outbound.Writer.TryWrite(JsonSerializer.Serialize(new WsEnvelope("analysis", result),
                    WsEndpoints.JsonOptions));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "analysis 重算失败: {Pair} {Interval}", pair.Display, interval);
            }
        }
    }
}
