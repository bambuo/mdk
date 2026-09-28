using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mdk.Api.Domain;
using Mdk.Api.Models;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Binance;

/// <summary>
/// 币安K线 WS 上游管理：每个 (市场, 交易对, 周期) 一条上游连接（浏览器多标签共享，引用计数，空闲自动断开），
/// 断线指数退避重连。
/// 兜底：若 WS 静默超过数秒（部分网络环境下 fstream 合约流只握手不推数据），
/// 自动改用 REST 轮询最新K线并推送，恢复与不依赖 WS 的实时性。
/// </summary>
public sealed class KlineStreamService : IAsyncDisposable
{
    private readonly BinanceOptions _options;
    private readonly BinanceRestClient _rest;
    private readonly ILogger<KlineStreamService> _logger;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Upstream> _streams = new(StringComparer.Ordinal);

    public KlineStreamService(IOptions<BinanceOptions> options, BinanceRestClient rest, ILogger<KlineStreamService> logger)
    {
        _options = options.Value;
        _rest = rest;
        _logger = logger;
    }

    /// <summary>订阅某市场某交易对某周期的实时K线；返回的 IDisposable 用于取消订阅。</summary>
    public IDisposable Subscribe(MarketKind market, TradingPair pair, string interval, Action<KlineUpdate> onKline)
    {
        var streamKey = $"{pair.Symbol.ToLowerInvariant()}@kline_{interval}";
        var key = $"{(market == MarketKind.Futures ? "fut" : "spot")}:{streamKey}";
        Upstream upstream;
        lock (_sync)
        {
            // 正在关闭的上游不可复用：若此时挂上去，它的取消令牌已触发，订阅方将永远收不到数据，
            // 而前端 WS 仍是连接状态（表现为"显示实时连接但行情不动"）。
            if (!_streams.TryGetValue(key, out var existing) || existing.IsClosing)
            {
                if (existing is not null) _streams.Remove(key);
                var wsBase = (market == MarketKind.Futures ? _options.FuturesWsBaseUrl : _options.WsBaseUrl).TrimEnd('/');
                var url = $"{wsBase}/ws/{streamKey}";
                var created = new Upstream(key, url, market, pair, interval, _rest, _logger);
                created.Closed = () =>
                {
                    lock (_sync)
                    {
                        // 仅移除自身，避免误删后来新建的同名上游
                        if (_streams.TryGetValue(key, out var current) && ReferenceEquals(current, created))
                            _streams.Remove(key);
                    }
                };
                existing = created;
                _streams[key] = created;
            }
            existing.AddRef();
            upstream = existing;
        }
        upstream.AddHandler(onKline);
        upstream.EnsureStarted();
        return new Subscription(upstream, onKline);
    }

    private sealed class Subscription(Upstream upstream, Action<KlineUpdate> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            upstream.RemoveHandler(handler);
            upstream.ReleaseRef();
        }
    }

    private sealed class Upstream : IAsyncDisposable
    {
        /// <summary>WS 静默超过该时长即启用 REST 轮询兜底。</summary>
        private static readonly TimeSpan WsSilenceThreshold = TimeSpan.FromSeconds(8);

        /// <summary>兜底轮询间隔。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        private readonly string _key;
        private readonly Uri _url;
        private readonly MarketKind _market;
        private readonly TradingPair _pair;
        private readonly string _interval;
        private readonly BinanceRestClient _rest;
        private readonly ILogger _logger;
        private readonly Lock _sync = new();
        private readonly List<Action<KlineUpdate>> _handlers = [];
        private readonly CancellationTokenSource _cts = new();

        private Task _loop = Task.CompletedTask;
        private Task _poller = Task.CompletedTask;
        private bool _started;
        private Timer? _idleTimer;
        private long _lastWsMessageTicks;
        private long _lastCandleTime;
        private bool _usingRestFallback;
        private volatile bool _closing;

        public Action? Closed { get; set; }

        /// <summary>是否已决定关闭（空闲超时）；为 true 时不可再被订阅，否则订阅方永远收不到数据。</summary>
        public bool IsClosing => _closing;

        public Upstream(string key, string url, MarketKind market, TradingPair pair, string interval, BinanceRestClient rest, ILogger logger)
        {
            _key = key;
            _url = new Uri(url);
            _market = market;
            _pair = pair;
            _interval = interval;
            _rest = rest;
            _logger = logger;
        }

        public void AddRef()
        {
            lock (_sync)
            {
                if (_idleTimer != null)
                {
                    _idleTimer.Dispose();
                    _idleTimer = null;
                }
            }
        }

        public void ReleaseRef()
        {
            lock (_sync)
            {
                if (_handlers.Count > 0) return; // 仍有订阅者，直接复用
                if (_idleTimer != null || _closing) return;
                // 30 秒后仍无人订阅则关闭上游
                _idleTimer = new Timer(
                    _ =>
                    {
                        lock (_sync)
                        {
                            if (_handlers.Count > 0 || _idleTimer == null) return;
                            _idleTimer.Dispose();
                            _idleTimer = null;
                            // 在锁内置位：此后新建订阅不会再复用本上游（见 Subscribe 的 IsClosing 判断）
                            _closing = true;
                        }
                        _logger.LogInformation("K线流空闲关闭: {Key}", _key);
                        Closed?.Invoke();
                        _ = DisposeAsync();
                    },
                    null,
                    TimeSpan.FromSeconds(30),
                    Timeout.InfiniteTimeSpan);
            }
        }

        public void AddHandler(Action<KlineUpdate> handler)
        {
            lock (_sync) _handlers.Add(handler);
        }

        public void RemoveHandler(Action<KlineUpdate> handler)
        {
            lock (_sync) _handlers.Remove(handler);
        }

        public void EnsureStarted()
        {
            lock (_sync)
            {
                if (_started) return;
                _started = true;
                _loop = RunAsync(_cts.Token);
                _poller = PollFallbackAsync(_cts.Token);
            }
        }

        private void Dispatch(KlineUpdate update)
        {
            Action<KlineUpdate>[] snapshot;
            lock (_sync) snapshot = [.. _handlers];
            foreach (var handler in snapshot)
            {
                try
                {
                    handler(update);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "K线分发回调异常: {Key}", _key);
                }
            }
        }

        /// <summary>
        /// REST 轮询兜底：WS 静默超过阈值时，直接轮询币安 REST 最新K线并分发；
        /// WS 恢复推送后自动停用。用于部分网络环境下合约 WS 流只握手不推数据的情况。
        /// </summary>
        private async Task PollFallbackAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PollInterval, ct);
                    var lastWs = Interlocked.Read(ref _lastWsMessageTicks);
                    var silent = lastWs == 0 || DateTimeOffset.UtcNow - new DateTimeOffset(lastWs, TimeSpan.Zero) > WsSilenceThreshold;
                    if (!silent)
                    {
                        _usingRestFallback = false;
                        continue;
                    }
                    if (!_usingRestFallback)
                    {
                        _usingRestFallback = true;
                        _logger.LogWarning("K线上游 {Key} 静默超过 {Seconds}s，启用 REST 轮询兜底", _key, WsSilenceThreshold.TotalSeconds);
                    }

                    var candles = await _rest.GetKlinesAsync(_market, _pair, _interval, 2, ct);
                    if (candles.Length == 0) continue;
                    var latest = candles[^1];
                    var previousTime = Interlocked.Read(ref _lastCandleTime);

                    // 若已换到新K线，先把上一根以收盘态补发（触发分析重算）
                    if (previousTime != 0 && latest.Time > previousTime && candles.Length >= 2 && candles[0].Time == previousTime)
                    {
                        var closed = candles[0];
                        Dispatch(new KlineUpdate(_market, _pair, _interval,
                            closed.Time, closed.Open, closed.High, closed.Low, closed.Close, closed.Volume, IsFinal: true));
                    }

                    Interlocked.Exchange(ref _lastCandleTime, latest.Time);
                    Dispatch(new KlineUpdate(_market, _pair, _interval,
                        latest.Time, latest.Open, latest.High, latest.Low, latest.Close, latest.Volume, IsFinal: false));
                }
            }
            catch (OperationCanceledException)
            {
                // 停止轮询
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "K线 REST 轮询兜底异常退出: {Key}", _key);
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var backoff = TimeSpan.FromSeconds(1);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    await ws.ConnectAsync(_url, ct);
                    _logger.LogInformation("币安K线流已连接: {Key}", _key);
                    backoff = TimeSpan.FromSeconds(1);

                    var buffer = new byte[16 * 1024];
                    while (true)
                    {
                        var message = await ReceiveFullTextAsync(ws, buffer, ct);
                        if (message is null) break;
                        Interlocked.Exchange(ref _lastWsMessageTicks, DateTimeOffset.UtcNow.UtcTicks);
                        ParseAndDispatch(message);
                    }
                    _logger.LogWarning("币安K线流被远端关闭: {Key}", _key);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "币安K线流异常，{Delay} 后重连: {Key}", backoff, _key);
                }

                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }

        private void ParseAndDispatch(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("k", out var k)) return;
                var symbolRaw = root.GetProperty("s").GetString() ?? "";
                if (!TradingPair.TryParse(symbolRaw, out var pair)) return;
                var update = new KlineUpdate(
                    _market,
                    pair,
                    k.GetProperty("i").GetString() ?? "",
                    k.GetProperty("t").GetInt64() / 1000,
                    Str(k, "o"),
                    Str(k, "h"),
                    Str(k, "l"),
                    Str(k, "c"),
                    Str(k, "v"),
                    k.GetProperty("x").GetBoolean());
                Dispatch(update);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "K线消息解析失败: {Key}", _key);
            }
        }

        private static double Str(JsonElement parent, string name) =>
            double.Parse(parent.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

        /// <summary>接收一条完整文本帧（WS 分片自动拼接），远端关闭返回 null。</summary>
        private static async Task<string?> ReceiveFullTextAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
        {
            using var output = new MemoryStream();
            while (true)
            {
                if (ws.State != WebSocketState.Open) return null;
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                output.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await Task.WhenAll(_loop, _poller);
            }
            catch
            {
                // 重连/轮询循环被取消属预期
            }
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Upstream[] snapshot;
        lock (_sync) snapshot = [.. _streams.Values];
        _streams.Clear();
        foreach (var upstream in snapshot)
            await upstream.DisposeAsync();
    }
}
