using Mdk.Api.Analysis;

namespace Mdk.Tests;

/// <summary>
/// 联合打分 v2 纯函数测试：特征按**信号类别**定义（1/2 类反转语义、3 类延续语义）、
/// 卖点镜像、指标未定义的边界、分档口径（低 ≤2 / 中 3 / 高 ≥4）。
/// </summary>
public static class JointScoreTests
{
    public static void Register(TestKit t)
    {
        t.Case("联合打分v2_反转类_逆势背驰极端全中=5", () =>
        {
            // 1买：价格在 EMA200 下方（逆势）、面积比 0.5（强背驰）、RSI 28（极端）、成本低、止损近
            var d = JointScoreRules.Compute(
                price: 90m, ema200: 100m, rsi: 28m, macdHist: -1m,
                side: "buy", kind: "1买", areaRatio: 0.5m, riskPct: 0.02m, lagShare: 0.4m);
            Assert.Equal(5, d.Score);
            Assert.Equal(5, d.MaxScore);
            Assert.All(d.Features, f => Assert.Equal(true, f.Hit));
        });

        t.Case("联合打分v2_延续类_顺势动量全中=5", () =>
        {
            // 3买：价格在 EMA200 上方（顺势）、RSI 55（顺向）、MACD 柱 ≥0，成本低、止损近
            var d = JointScoreRules.Compute(
                price: 110m, ema200: 100m, rsi: 55m, macdHist: 1m,
                side: "buy", kind: "3买", areaRatio: null, riskPct: 0.02m, lagShare: 0.4m);
            Assert.Equal(5, d.Score);
            Assert.All(d.Features, f => Assert.Equal(true, f.Hit));
        });

        t.Case("联合打分v2_卖点镜像_反转与延续", () =>
        {
            var reversal = JointScoreRules.Compute(110m, 100m, 72m, 1m, "sell", "1卖", 0.6m, 0.02m, 0.4m);
            Assert.Equal(5, reversal.Score);   // 价格在 EMA200 上方（逆势）+ RSI 72 ≥ 65 + 背驰 + 成本 + 效率
            var continuation = JointScoreRules.Compute(90m, 100m, 45m, -1m, "sell", "3卖", null, 0.02m, 0.4m);
            Assert.Equal(5, continuation.Score);
        });

        t.Case("联合打分v2_全不中=0", () =>
        {
            // 1买：价格在 EMA200 上方（非逆势）、无背驰、RSI 50（非极端）、成本高、止损远
            var d = JointScoreRules.Compute(110m, 100m, 50m, -1m, "buy", "1买", null, 0.04m, 0.9m);
            Assert.Equal(0, d.Score);
            Assert.All(d.Features, f => Assert.Equal(false, f.Hit));
        });

        t.Case("联合打分v2_指标未定义不误判", () =>
        {
            // EMA200/RSI 为 null：对应特征不命中也不崩溃；成本低 + 止损效率仍命中
            // 面积比 0.5 是缠论结构度量（非指标），仍命中；EMA200/RSI 未定义 → 对应特征不命中也不崩溃
            var d = JointScoreRules.Compute(90m, null, null, null, "buy", "1买", 0.5m, 0.02m, 0.4m);
            Assert.Equal(3, d.Score);
            Assert.Equal(false, d.Features[0].Hit);
            Assert.Equal(true, d.Features[1].Hit);
            Assert.Equal(false, d.Features[2].Hit);
        });

        t.Case("联合打分v2_阈值边界_恰好命中", () =>
        {
            // 面积比恰为 0.7、RSI 恰为 35、lagShare 恰为 0.5、riskPct 恰为 2.5% → 全部按命中计
            var d = JointScoreRules.Compute(90m, 100m, 35m, -1m, "buy", "2买", 0.7m, 0.025m, 0.5m);
            Assert.Equal(5, d.Score);
        });

        t.Case("滞后占比与风险单位_计算与边界", () =>
        {
            Assert.Equal(0.5m, JointScoreRules.LagShare(100m, 95m, 90m)!.Value, 6);
            Assert.Equal(true, JointScoreRules.LagShare(100m, 95m, 100m) is null);
            Assert.Equal(true, JointScoreRules.LagShare(100m, null, 90m) is null);
            Assert.Equal(0.1m, JointScoreRules.RiskPct(100m, 90m)!.Value, 6);
            Assert.Equal(true, JointScoreRules.RiskPct(100m, null) is null);
            Assert.Equal(true, JointScoreRules.RiskPct(0m, 90m) is null);
        });
    }
}
