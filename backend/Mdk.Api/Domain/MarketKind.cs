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
    public static string? LowerInterval(string interval) => interval switch
    {
        "15m" or "30m" => "5m",
        "1h" or "2h" => "15m",
        "4h" or "6h" => "1h",
        "12h" => "4h",
        "1d" or "3d" => "4h",
        "1w" => "1d",
        _ => null,
    };

    /// <summary>多周期共振用的高一档周期；最高档（1w）返回 null。</summary>
    public static string? HigherInterval(string interval) => interval switch
    {
        "5m" or "15m" or "30m" => "1h",
        "1h" or "2h" => "4h",
        "4h" or "6h" or "12h" => "1d",
        "1d" or "3d" => "1w",
        _ => null,
    };
}
