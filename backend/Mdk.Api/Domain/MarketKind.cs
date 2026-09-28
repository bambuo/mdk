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
}
