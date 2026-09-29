namespace Mdk.Api.Domain;

/// <summary>
/// 锚定币（稳定币与法币锚定资产）判定：这类交易币的价格被设计成恒定（≈1 计价单位），
/// K 线几乎是一条直线，**不构成技术分析标的**——它们既不会形成有效结构，
/// 也不会给出有意义的买卖点；把它们计入样本会污染绩效统计（拉平均值、虚增样本量）。
///
/// 规则属于领域知识，因此落在领域层：后端（自选分析/回填/落库/统计）与前端（默认标的选择）共用同一判定。
/// 判定分两类：① 显式清单（USD 系稳定币与法币）；② 形如 USDx / USD&lt;数字&gt; 的包装稳定币。
/// </summary>
public static class PeggedAssets
{
    /// <summary>显式清单：主要稳定币与法币（按币安实际交易的计价资产整理）。</summary>
    private static readonly HashSet<string> Explicit = new(StringComparer.Ordinal)
    {
        // 美元稳定币
        "USDT", "USDC", "FDUSD", "TUSD", "USDP", "BUSD", "DAI", "USDE", "USDS", "USD1", "XUSD", "USDY", "PYUSD", "USDD",
        // 法币
        "EUR", "EURI", "AEUR", "GBP", "TRY", "BRL", "ARS", "JPY", "AUD", "IDRT", "NGN", "UAH", "PLN", "RON", "CZK", "MXN", "ZAR",
    };

    /// <summary>是否为锚定币（大小写不敏感）。</summary>
    public static bool IsPegged(string baseAsset)
    {
        var asset = baseAsset.Trim().ToUpperInvariant();
        if (asset.Length == 0) return false;
        if (Explicit.Contains(asset)) return true;
        // 包装稳定币：USDx（如 USDT 变体以外的 4 字符）或 USD+数字（如 USD1）
        return asset.StartsWith("USD", StringComparison.Ordinal)
               && (asset.Length == 4 || (asset.Length > 4 && char.IsAsciiDigit(asset[3])));
    }
}
