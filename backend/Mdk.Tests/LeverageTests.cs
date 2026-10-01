using Mdk.Api.Analysis;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>
/// 杠杆面事实的纯计算：主动买占比、基差、持仓变化、年化费率。
/// 这些是**事实陈述**（回答"当前位置是否拥挤"），不产生信号、不进台账——故此处只锁口径。
/// </summary>
public static class LeverageTests
{
    public static void Register(TestKit t)
    {
        t.Case("杠杆面_主动买占比_按近N根成交量加权", () =>
        {
            var candles = new List<Candle>
            {
                Bar(volume: 10, takerBuy: 10),   // 旧：不该计入（bars=2 只取最近 2 根）
                Bar(volume: 10, takerBuy: 0),
                Bar(volume: 30, takerBuy: 15),   // 合计：量 40、主动买 15 → 0.375
            };

            Assert.Equal(0.375m, LeverageMath.TakerBuyShare(candles, bars: 2)!.Value);
        });

        t.Case("杠杆面_主动买占比_样本不足时用全部_无量时为空", () =>
        {
            var few = new List<Candle> { Bar(volume: 4, takerBuy: 1), Bar(volume: 4, takerBuy: 3) };
            Assert.Equal(0.5m, LeverageMath.TakerBuyShare(few, bars: 24)!.Value);

            var empty = new List<Candle> { Bar(volume: 0, takerBuy: 0) };
            Assert.True(LeverageMath.TakerBuyShare(empty) is null, "无成交量时应返回 null 而不是 0");
            Assert.True(LeverageMath.TakerBuyShare([]) is null, "空序列应返回 null");
        });

        t.Case("杠杆面_基差_标记价相对指数价", () =>
        {
            Assert.Equal(0.1m, LeverageMath.BasisPct(1000m, 999m)!.Value, 3);      // 0.100%
            Assert.Equal(-0.2m, LeverageMath.BasisPct(998m, 1000m)!.Value, 3);     // -0.200%
            Assert.True(LeverageMath.BasisPct(1000m, 0m) is null, "指数价为 0 时应为空");
        });

        t.Case("杠杆面_持仓量变化与年化费率", () =>
        {
            Assert.Equal(10m, LeverageMath.ChangePct(100m, 110m)!.Value);
            Assert.Equal(-50m, LeverageMath.ChangePct(100m, 50m)!.Value);
            Assert.True(LeverageMath.ChangePct(0m, 50m) is null, "起点为 0 时应为空");

            // 0.0001/8h × 3 次/日 × 365 日 = 10.95%/年
            Assert.Equal(10.95m, LeverageMath.AnnualizedFundingPct(0.0001m)!.Value);
            Assert.True(LeverageMath.AnnualizedFundingPct(null) is null);
        });
    }

    private static Candle Bar(decimal volume, decimal takerBuy) =>
        new(1_700_000_000L, 100m, 101m, 99m, 100m, volume, takerBuy);
}
