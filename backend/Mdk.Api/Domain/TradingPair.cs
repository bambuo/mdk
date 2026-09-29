using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Mdk.Api.Domain;

/// <summary>
/// 交易对值对象，由交易币（BaseAsset，被交易的标的币）与计价币（QuoteAsset，标价货币）组成。
/// 例：BTC/USDT —— 用 USDT 买卖 BTC。
/// 全项目统一用本类型表示交易对（接口参数、币安客户端、WS 订阅、分析结果），禁止裸字符串；
/// 序列化为币安连写格式 "BTCUSDT"，展示为 "BTC/USDT"。
/// 交易币/计价币拆分的权威来源是币安 exchangeInfo（见 SymbolCatalog），
/// 本类型的连写解析只作为快速入口，按已知计价币后缀切分。
/// </summary>
public readonly record struct TradingPair : IComparable<TradingPair>
{
    /// <summary>已知计价币集合，按长度降序排列后用于连写切分（长后缀优先，如 FDUSD 先于 USDT 尝试）。</summary>
    private static readonly string[] KnownQuotes = BuildKnownQuotes();

    private static string[] BuildKnownQuotes()
    {
        string[] candidates =
        [
            "USDT", "USDC", "FDUSD", "TUSD", "BUSD", "DAI", "EURI", "AEUR", "USDP",
            "EUR", "TRY", "BRL", "ARS", "IDRT", "NGN", "UAH", "PLN", "RON", "CZK",
            "MXN", "ZAR", "JPY", "GBP", "AUD", "XBT",
            "BNB", "BTC", "ETH", "TRX", "XRP", "DOT", "SOL", "DOGE", "LTC",
        ];
        return candidates
            .OrderByDescending(q => q.Length)
            .ThenBy(q => q, StringComparer.Ordinal)
            .ToArray();
    }

    private TradingPair(string baseAsset, string quoteAsset)
    {
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
    }

    /// <summary>交易币（被交易的标的币），BTC/USDT 中的 BTC。</summary>
    public string BaseAsset { get; }

    /// <summary>计价币（标价货币），BTC/USDT 中的 USDT。</summary>
    public string QuoteAsset { get; }

    /// <summary>币安接口格式：BASE+QUOTE 连写，如 BTCUSDT。</summary>
    public string Symbol => string.Concat(BaseAsset, QuoteAsset);

    /// <summary>展示格式：BTC/USDT。</summary>
    public string Display => $"{BaseAsset}/{QuoteAsset}";

    /// <summary>交易币是否为锚定币（见 <see cref="PeggedAssets"/>）：锚定币不构成分析标的。</summary>
    public bool IsPegged => PeggedAssets.IsPegged(BaseAsset);

    /// <summary>由拆分后的交易币与计价币构造，两个入参均会做大写与字符合法性归一。</summary>
    public static TradingPair From(string baseAsset, string quoteAsset) =>
        new(Normalize(baseAsset, nameof(baseAsset)), Normalize(quoteAsset, nameof(quoteAsset)));

    /// <summary>解析币安连写格式（如 "BTCUSDT"，大小写不敏感）。无法按已知计价币切分时抛 <see cref="FormatException"/>。</summary>
    public static TradingPair Parse(string raw) =>
        TryParse(raw, out var pair)
            ? pair
            : throw new FormatException($"无法识别的交易对 “{raw}”，请使用 BASEQUOTE 连写格式（如 BTCUSDT）。");

    public static bool TryParse(string? raw, out TradingPair pair)
    {
        pair = default;
        var s = raw?.Trim().ToUpperInvariant() ?? string.Empty;
        if (s.Length < 5) return false;
        foreach (var quote in KnownQuotes)
        {
            if (!s.EndsWith(quote, StringComparison.Ordinal)) continue;
            var baseAsset = s[..^quote.Length];
            if (baseAsset.Length == 0) return false;
            pair = new TradingPair(baseAsset, quote);
            return true;
        }
        return false;
    }

    /// <summary>Minimal API 自定义绑定：端点可直接声明 TradingPair? 参数，从 query 的 symbol 读取。</summary>
    public static ValueTask<TradingPair?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        var raw = context.Request.Query["symbol"].ToString();
        return ValueTask.FromResult(TryParse(raw, out var pair) ? pair : (TradingPair?)null);
    }

    private static string Normalize(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        var s = value.Trim().ToUpperInvariant();
        // 币安存在 BROCCOLI714、1MBABYDOGE 等较长的字母数字交易币，放宽到 20 位
        if (s.Length > 20 || !s.All(char.IsAsciiLetterOrDigit))
            throw new FormatException($"非法的币种代码 “{value}”。");
        return s;
    }

    public int CompareTo(TradingPair other) => string.CompareOrdinal(Symbol, other.Symbol);

    public override string ToString() => Display;
}
