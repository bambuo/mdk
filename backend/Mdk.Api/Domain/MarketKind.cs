using System.Text.Json.Serialization;

namespace Mdk.Api.Domain;

/// <summary>市场类型：现货 / U 本位合约（永续）。</summary>
public enum MarketKind
{
    Spot,
    Futures,
}

public static class MarketKindExtensions
{
    /// <summary>解析 market 查询参数（spot / futures，默认 spot）。</summary>
    public static bool TryParse(string? raw, out MarketKind market)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "spot":
            case "":
                market = MarketKind.Spot;
                return true;
            case "futures":
                market = MarketKind.Futures;
                return true;
            default:
                market = MarketKind.Spot;
                return false;
        }
    }

    /// <summary>币安 REST 路径前缀：现货 /api/v3，U 本位合约 /fapi/v1。</summary>
    public static string ToRestPath(this MarketKind market) => market switch
    {
        MarketKind.Futures => "/fapi/v1",
        _ => "/api/v3",
    };
}

public static class MarketIntervals
{
    public static readonly string[] All = ["5m", "15m", "30m", "1h", "2h", "4h", "6h", "12h", "1d", "3d", "1w"];

    public static bool IsValid(string interval) => Array.IndexOf(All, interval) >= 0;

    /// <summary>周期对应的秒数。</summary>
    public static long IntervalSeconds(string interval) => interval switch
    {
        "5m" => 300,
        "15m" => 900,
        "30m" => 1800,
        "1h" => 3600,
        "2h" => 7200,
        "4h" => 14400,
        "6h" => 21600,
        "12h" => 43200,
        "1d" => 86400,
        "3d" => 259200,
        "1w" => 604800,
        _ => 3600,
    };

    /// <summary>
    /// 次级别（下一档周期）；最小档（5m）返回 null。
    /// 用于"次级别确认"：本级别买卖点需次级别结构同向（缠论中本级别与次级别的关系）。
    /// </summary>
    /// <summary>
    /// 低一档周期（次级别确认与级别对照用）；最低档（5m）返回 null。
    ///
    /// **2026-09-30 起取相邻档**（用户拍板）：与 <see cref="HigherInterval"/> 完全对称，
    /// 使"高级别 ↔ 本级别 ↔ 次级别"呈自相似关系（缠论本级别/次级别的原意）。
    /// 此前为跨档（1h→15m、4h→1h，约 4 倍），与改后的高级别不对称。
    /// **注意**：次级别档位决定"次级别确认"的严格度，因此会改变**哪些信号被产出**——
    /// 切换后旧样本（无论 live 还是 backfill）的产出规则与当前不一致，台账已按新口径重建。
    /// </summary>
    public static string? LowerInterval(string interval) => interval switch
    {
        "15m" => "5m",
        "30m" => "15m",
        "1h" => "30m",
        "2h" => "1h",
        "4h" => "2h",
        "6h" => "4h",
        "12h" => "6h",
        "1d" => "12h",
        "3d" => "1d",
        "1w" => "3d",
        _ => null,
    };

    /// <summary>
    /// 高一档周期（级别对照与级别共振用）；最高档（1w）返回 null。
    ///
    /// **2026-09-30 起取相邻档**（用户拍板）：与 <see cref="LowerInterval"/> 对称，
    /// 使"本级别 ↔ 高级别"呈自相似关系（缠论本级别/次级别的原意）。
    /// 此前高级别取**粗跳档**（5m/15m/30m → 1h），导致 5m 与高级别相差 12 倍，
    /// 级别对照的"一致/分歧"意义被削弱。切换时 5m/15m/30m 样本合计不足 150 条，重跑代价极小。
    /// </summary>
    public static string? HigherInterval(string interval) => interval switch
    {
        "5m" => "15m",
        "15m" => "30m",
        "30m" => "1h",
        "1h" => "4h",
        "2h" => "4h",
        "4h" => "1d",
        "6h" => "1d",
        "12h" => "1d",
        "1d" => "1w",
        "3d" => "1w",
        _ => null,
    };
}
