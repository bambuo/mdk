using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Mdk.Api.Analysis;
using Mdk.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Mdk.Api.Notify;

/// <summary>
/// 飞书提醒器：订阅监控列表信号广播器（**已过滤**的信号：监控列表内 / 已确认 / 缠论 / 实时落库），
/// 经同向冷却后异步推送到飞书自定义机器人。
///
/// 为什么用后台队列：广播事件在 SignalStore 的**写锁内**同步触发，任何阻塞式 HTTP 都会拖慢台账写入；
/// 因此这里只做入队（非阻塞），由 <see cref="ExecuteAsync"/> 循环异步发送。
///
/// 未配置 <see cref="FeishuOptions.WebhookUrl"/> 时完全惰性：不入队、不发请求。
/// </summary>
public sealed class FeishuNotifier : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly FeishuOptions _options;
    private readonly SignalCooldown _cooldown;
    private readonly ILogger<FeishuNotifier> _logger;
    private readonly Channel<NotifiableSignal> _queue = Channel.CreateUnbounded<NotifiableSignal>(
        new UnboundedChannelOptions { SingleReader = true });

    public FeishuNotifier(
        HttpClient http,
        IOptions<FeishuOptions> options,
        WatchlistSignalBroadcaster broadcaster,
        ILogger<FeishuNotifier> logger)
    {
        _http = http;
        _options = options.Value;
        _cooldown = new SignalCooldown(_options.CooldownMinutes);
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 3, 60));
        broadcaster.Published += OnPublished;
        if (_options.IsConfigured)
            _logger.LogInformation("飞书提醒已启用（同向冷却 {Minutes} 分钟）", _options.CooldownMinutes);
        else
            _logger.LogInformation(
                "飞书提醒未配置（Feishu:WebhookUrl 为空）——跳过发送；" +
                "填写 {Local}（本机，已 gitignore）或设环境变量 Feishu__WebhookUrl 后重启即可启用",
                LocalSettings.FileName);
    }

    /// <summary>入队（非阻塞；在 SignalStore 写锁内被调用）。</summary>
    private void OnPublished(NotifiableSignal signal)
    {
        if (!_options.IsConfigured) return;
        _queue.Writer.TryWrite(signal);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var signal in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAsync(signal, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 通知失败绝不影响台账与其它链路
                _logger.LogWarning(ex, "飞书提醒发送失败 {Symbol} {Interval}", signal.Symbol, signal.Interval);
            }
        }
    }

    /// <summary>发一条（含冷却判定）。返回是否真的发出，便于测试与自检端点复用。</summary>
    public async Task<(bool Sent, string Detail)> SendAsync(NotifiableSignal signal, CancellationToken ct)
    {
        if (!_options.IsConfigured) return (false, "未配置 Feishu:WebhookUrl");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!_cooldown.TryAcquire(signal.Symbol, signal.Interval, signal.Side, now))
            return (false, $"同向冷却中（{_options.CooldownMinutes} 分钟）");

        var secret = string.IsNullOrWhiteSpace(_options.Secret) ? null : _options.Secret;
        var body = FeishuMessage.BuildBody(signal, now, secret);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_options.WebhookUrl, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return (false, $"HTTP {(int)response.StatusCode}: {Trim(text)}");

        // 飞书成功响应：{"code":0,"msg":"success"}（也可能是 {"StatusCode":0,...} 的老格式）
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32()
                : root.TryGetProperty("StatusCode", out var sc) ? sc.GetInt32()
                : 0;
            var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
            return code == 0
                ? (true, "已发送")
                : (false, $"飞书返回 code={code} msg={msg}（常见：19021 签名校验失败 / 9499 频率限制 / 19024 关键词不匹配）");
        }
        catch (JsonException)
        {
            return (true, $"已发送（响应非 JSON：{Trim(text)}）");
        }
    }

    /// <summary>自检：发一条示例卡片（不参与冷却，供 /api/notify/feishu/test 使用）。</summary>
    public async Task<(bool Ok, string Detail)> SendTestAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return (false, $"未配置 Feishu:WebhookUrl（写入 {LocalSettings.FileName} 或设环境变量 Feishu__WebhookUrl 后重试）");
        var sample = new NotifiableSignal(
            Market: "spot", Symbol: "BTCUSDT", BaseAsset: "BTC", QuoteAsset: "USDT", Interval: "1h",
            Kind: "3买", Side: "buy", Time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Price: 83000m, StopPrice: 82800m, Note: "这是一条自检消息：链路已打通");
        var secret = string.IsNullOrWhiteSpace(_options.Secret) ? null : _options.Secret;
        var body = FeishuMessage.BuildBody(sample, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), secret);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.PostAsync(_options.WebhookUrl, content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return (false, $"HTTP {(int)response.StatusCode}: {Trim(text)}");
            using var doc = JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : null;
            return code == 0 ? (true, "自检消息已发送，请查看飞书群") : (false, $"飞书返回 code={code} msg={msg}");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Trim(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>供自检端点展示当前配置状态（不泄露完整 Webhook 地址）。</summary>
    public string Describe()
    {
        if (!_options.IsConfigured) return "未配置";
        var url = _options.WebhookUrl;
        var tail = url.Length > 6 ? url[^6..] : url;
        return $"已配置（…{tail}，签名校验={(_options.Secret.Length > 0 ? "开" : "关")}，同向冷却 {_options.CooldownMinutes} 分钟）";
    }

    /// <summary>把消息体解析回对象（测试用；避免测试重复实现 JSON 解析细节）。</summary>
    internal static JsonDocument ParseBody(string body) => JsonDocument.Parse(body);

    /// <summary>时间戳口径：飞书要求秒级（毫秒会验签失败）。</summary>
    internal static string TimestampText(long unixSeconds) => unixSeconds.ToString(CultureInfo.InvariantCulture);
}
