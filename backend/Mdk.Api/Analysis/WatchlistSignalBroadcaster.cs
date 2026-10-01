using System.Threading.Channels;
using Mdk.Api.Notify;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mdk.Api.Domain;

namespace Mdk.Api.Analysis;

/// <summary>
/// 监控列表信号广播器：把"监控列表内**新入库**的已确认缠论买卖点"实时推给订阅者（SSE 端点用）。
///
/// 数据流：`SignalStore.SignalRecorded` 事件（新入库才触发）→ 本广播器按三道过滤
/// （来源=缠论、已确认、origin=live——回填/重放的历史样本不打扰）→ 监控列表成员校验 → 推给所有订阅通道。
///
/// 选择 **SSE** 而非 WebSocket：通知是单向的（服务端 → 前端），SSE 更简单且自带断线重连；
/// 图表 K 线的双向通道仍是 WS，两者并存（2026-09-30 用户拍板，替代 60s 前端轮询）。
/// </summary>
public sealed class WatchlistSignalBroadcaster : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly WatchlistStore _store;
    private readonly SignalStore _signals;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, Channel<string>> _clients = new();
    private bool _disposed;

    public WatchlistSignalBroadcaster(WatchlistStore store, SignalStore signals)
    {
        _store = store;
        _signals = signals;
        // 事件在 SignalStore 的锁内触发：本处理器不得再回调 SignalStore（无此依赖），且整体 try/catch 隔离异常
        signals.SignalRecorded += OnSignalRecorded;
    }

    /// <summary>
    /// 已过滤信号的发布事件（SSE 与外部通知器共用**同一套过滤**——不复制过滤口径）。
    /// 在 SignalStore 的写锁内同步触发，订阅方必须**非阻塞**（外部通知走后台队列，见 FeishuNotifier）。
    /// </summary>
    public event Action<NotifiableSignal>? Published;

    /// <summary>订阅：返回通道读取端与订阅 Id（取消订阅时用）。</summary>
    public Guid Subscribe(out ChannelReader<string> reader)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WatchlistSignalBroadcaster));
            _clients[id] = channel;
        }

        reader = channel.Reader;
        return id;
    }

    public void Unsubscribe(Guid id)
    {
        lock (_sync)
        {
            if (_clients.Remove(id, out var channel)) channel.Writer.TryComplete();
        }
    }

    private void OnSignalRecorded(SignalEntry e)
    {
        try
        {
            // 过滤：只推"监控列表内、已确认、缠论、实时落库"的买卖点。
            // origin=live 过滤掉回填/重放的历史样本——历史批量入库时不能向在线客户端刷屏。
            if (e.Source != "缠论" || !e.IsConfirmed || e.Origin != "live") return;

            var watched = _store.All().Any(x => x.Market == e.Market && x.Pair == e.Pair && x.Enabled);
            if (!watched) return;

            var signal = new NotifiableSignal(
                Market: MarketKey(e.Market),
                Symbol: e.Pair.Symbol,
                BaseAsset: e.Pair.BaseAsset,
                QuoteAsset: e.Pair.QuoteAsset,
                Interval: e.Interval,
                Kind: e.Kind,
                Side: e.Side,
                Time: e.Time,
                Price: e.Price,
                StopPrice: e.StopPrice,
                Note: e.Note);

            lock (_sync)
            {
                foreach (var channel in _clients.Values) channel.Writer.TryWrite(JsonSerializer.Serialize(signal, JsonOpts));
            }

            // 外部通知器（飞书等）：订阅方必须非阻塞，异常与它无关地隔离
            Published?.Invoke(signal);
        }
        catch (Exception)
        {
            // 广播失败不影响台账写入（事件在锁内触发，绝不让异常上抛破坏落库）
        }
    }

    private static string MarketKey(MarketKind market) => market.ToString().ToLowerInvariant();

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var channel in _clients.Values) channel.Writer.TryComplete();
            _clients.Clear();
        }

        _signals.SignalRecorded -= OnSignalRecorded;
    }
}
