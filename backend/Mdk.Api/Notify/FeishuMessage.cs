using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mdk.Api.Notify;

/// <summary>
/// 飞书（Feishu/Lark）自定义机器人配置。**未配置即禁用**：<see cref="WebhookUrl"/> 为空时不发送任何请求。
///
/// 凭据**不得写入受版本控制的 appsettings.json**（Webhook 等价于该群的发消息权限，随提交进入 git 历史即等于公开）。
/// 填写位置见 <see cref="Mdk.Api.Configuration.LocalSettings"/>：
/// 本机写 appsettings.Local.json（已 gitignore），生产用环境变量 <c>Feishu__WebhookUrl</c> / <c>Feishu__Secret</c>。
/// <code>
/// {
///   "Feishu": {
///     "WebhookUrl": "https://open.feishu.cn/open-apis/bot/v2/hook/xxxxxxxx",
///     "Secret": "签名校验密钥（机器人开启"签名校验"时必填，否则留空）"
///   }
/// }
/// </code>
/// </summary>
public sealed class FeishuOptions
{
    public const string SectionName = "Feishu";

    /// <summary>自定义机器人 Webhook 地址；为空则整体禁用（不发请求、不占资源）。</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>签名校验密钥（飞书机器人开启"签名校验"时必填；为空则不带时间戳与签名）。</summary>
    public string Secret { get; set; } = "";

    /// <summary>同标的同周期同方向的冷却分钟数（与前端桌面提醒同一口径：避免同向信号连续刷屏）。</summary>
    public int CooldownMinutes { get; set; } = 30;

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);
}

/// <summary>
/// 飞书签名（纯函数，可独立测试）。
///
/// 口径来自飞书开放平台：签名原文为 <c>timestamp\nsecret</c>（UTF-8），
/// 以其为 **HMAC-SHA256 的密钥**、**消息体为空** 计算摘要，再 Base64 编码（不是十六进制）。
/// timestamp 为**秒级**，且须与飞书服务器时间相差 1 小时以内。
/// </summary>
public static class FeishuSign
{
    /// <summary>计算签名。secret 为空时调用方不应发送签名参数。</summary>
    public static string Compute(long timestampSeconds, string secret)
    {
        var key = Encoding.UTF8.GetBytes($"{timestampSeconds.ToString(CultureInfo.InvariantCulture)}\n{secret}");
        var mac = HMACSHA256.HashData(key, ReadOnlySpan<byte>.Empty);   // 消息体为空
        return Convert.ToBase64String(mac);
    }
}

/// <summary>一条待通知的结构信号（由监控列表信号广播器在过滤后发布）。</summary>
public sealed record NotifiableSignal(
    string Market,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Interval,
    string? Kind,
    string Side,
    long Time,
    decimal Price,
    decimal? StopPrice,
    string? Note);

/// <summary>
/// 飞书消息体构造（纯函数，可独立测试）：把结构信号渲染为一张消息卡片。
///
/// 卡片口径：买点绿色 / 卖点红色（Header template green / red），标题写"标的 周期 类别"，
/// 正文给出方向、价格、止损参考、时间与结构说明；脚注固定带"结构分析提示，不构成投资建议"。
/// </summary>
public static class FeishuMessage
{
    /// <summary>构造发给飞书的完整请求体（含签名参数——仅在配置了 secret 时附带）。</summary>
    public static string BuildBody(NotifiableSignal s, long timestampSeconds, string? secret, string timeZoneOffsetLabel = "UTC+8")
    {
        var root = new JsonObject
        {
            ["timestamp"] = secret is null ? null : timestampSeconds.ToString(CultureInfo.InvariantCulture),
            ["sign"] = secret is null ? null : FeishuSign.Compute(timestampSeconds, secret),
            ["msg_type"] = "interactive",
            ["card"] = BuildCard(s, timeZoneOffsetLabel),
        };
        // 未配置签名时移除 null 字段（飞书对多余的空值参数会报参数错误）
        if (secret is null)
        {
            root.Remove("timestamp");
            root.Remove("sign");
        }
        return root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>卡片主体（纯结构，便于单测逐字段核对）。</summary>
    public static JsonObject BuildCard(NotifiableSignal s, string timeZoneOffsetLabel = "UTC+8")
    {
        var isBuy = s.Side == "buy";
        var direction = isBuy ? "买点" : "卖点";
        var kind = string.IsNullOrEmpty(s.Kind) ? "" : $" · {s.Kind}";
        var localTime = DateTimeOffset.FromUnixTimeSeconds(s.Time)
            .ToOffset(TimeSpan.FromHours(8))
            .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

        var lines = new List<string>
        {
            $"**方向**　{kind.TrimStart(' ', '·')} {direction}",
            $"**价格**　{s.Price}",
        };
        if (s.StopPrice is { } stop)
            lines.Add($"**止损参考**　{stop}");
        lines.Add($"**信号时间（{timeZoneOffsetLabel}）**　{localTime}");

        var elements = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "div",
                ["text"] = new JsonObject { ["tag"] = "lark_md", ["content"] = string.Join("\n", lines) },
            },
        };
        if (!string.IsNullOrWhiteSpace(s.Note))
        {
            elements.Add(new JsonObject { ["tag"] = "hr" });
            elements.Add(new JsonObject
            {
                ["tag"] = "note",
                ["elements"] = new JsonArray
                {
                    new JsonObject { ["tag"] = "plain_text", ["content"] = s.Note },
                },
            });
        }
        elements.Add(new JsonObject
        {
            ["tag"] = "note",
            ["elements"] = new JsonArray
            {
                new JsonObject { ["tag"] = "plain_text", ["content"] = "结构分析提示，不构成投资建议" },
            },
        });

        return new JsonObject
        {
            ["config"] = new JsonObject { ["wide_screen_mode"] = true },
            ["header"] = new JsonObject
            {
                ["template"] = isBuy ? "green" : "red",
                ["title"] = new JsonObject
                {
                    ["tag"] = "plain_text",
                    ["content"] = $"{s.BaseAsset}/{s.QuoteAsset} {s.Interval} {direction}",
                },
            },
            ["elements"] = elements,
        };
    }
}

/// <summary>
/// 同向冷却（纯逻辑，可测试）：同一 标的+周期+方向 在窗口内只允许通知一次；
/// **反向不冷却**（多空翻转比同向重复更值得知道）。与前端桌面提醒同一口径。
/// </summary>
public sealed class SignalCooldown(int cooldownMinutes)
{
    private readonly Dictionary<string, long> _lastSent = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly long _windowSeconds = Math.Max(1, cooldownMinutes) * 60L;

    /// <summary>是否允许发送；允许时会记录本次时间（同键后续调用在窗口内返回 false）。</summary>
    public bool TryAcquire(string symbol, string interval, string side, long nowSeconds)
    {
        var key = $"{symbol}|{interval}|{side}";
        lock (_sync)
        {
            if (_lastSent.TryGetValue(key, out var last) && nowSeconds - last < _windowSeconds) return false;
            _lastSent[key] = nowSeconds;
            return true;
        }
    }
}
