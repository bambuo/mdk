using Mdk.Api.Analysis;

namespace Mdk.Tests;

/// <summary>
/// 关注度判定测试（确定性）：中枢内＝不关注、远离中枢＝不追、贴边＋新鲜 3 类＝重点关注、
/// 1 类＝可关注（强背驰才点明）、2 类＝可关注、贴边无新鲜买卖点＝等确认、已走 &gt;1R 附加提示。
/// </summary>
public static class AttentionTests
{
    public static void Register(TestKit t)
    {
        t.Case("关注度_中枢内_暂不关注", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "in", atr: null, kind: null));
            Assert.Equal("none", v.Level);
            Assert.Contains("中枢内", v.Headline);
            Assert.Contains("多空成本区", v.Why);
        });

        t.Case("关注度_远离中枢_不追（趋势中段）", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "above", atr: 3.2m, kind: null));
            Assert.Equal("chase", v.Level);
            Assert.Contains("趋势中段", v.Headline);
            Assert.Contains("回抽到中枢边缘", v.How);
        });

        t.Case("关注度_贴边_新鲜3类_重点关注", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "above", atr: 0.3m, kind: "3买", bars: 2));
            Assert.Equal("focus", v.Level);
            Assert.Contains("3买", v.Headline);
            Assert.Contains("止损放结构失效位", v.How);
            Assert.Equal(true, v.Dont is null);
        });

        t.Case("关注度_贴边3类但已走超过1R_附加勿进提示", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "above", atr: 0.3m, kind: "3卖", bars: 3, movedR: 1.4m));
            Assert.Equal("focus", v.Level);
            Assert.Contains("不宜再进", v.Dont!);
        });

        t.Case("关注度_一类带强背驰_可以关注并点明面积比", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "below", atr: 0.2m, kind: "1买", bars: 1, areaRatio: 0.55m));
            Assert.Equal("watch", v.Level);
            Assert.Contains("面积比 0.55", v.Headline);
            Assert.Contains("等第 2 类确认", v.How);
        });

        t.Case("关注度_一类背驰偏弱_不点明面积比", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "below", atr: 0.2m, kind: "1卖", bars: 1, areaRatio: 0.95m));
            Assert.Equal("watch", v.Level);
            Assert.Contains("背驰偏弱", v.Headline);
        });

        t.Case("关注度_二类_可以关注", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "above", atr: 0.4m, kind: "2买", bars: 5));
            Assert.Equal("watch", v.Level);
            Assert.Contains("回抽确认", v.Headline);
        });

        t.Case("关注度_买卖点不新鲜_降为等确认", () =>
        {
            var v = AttentionRules.Evaluate(Position(zone: "above", atr: 0.3m, kind: "3买", bars: 40));
            Assert.Equal("wait", v.Level);
            Assert.Contains("等确认", v.Headline);
            Assert.Contains("位置对了，触发条件没到", v.Why);
        });

        t.Case("关注度_贴边但无买卖点_等确认", () =>
        {
            // atr 0.4 ≤ 0.5 → 属"贴边但无买卖点"：位置对了、触发条件没到
            var v = AttentionRules.Evaluate(Position(zone: "below", atr: 0.4m, kind: null));
            Assert.Equal("wait", v.Level);
            Assert.Contains("触发条件没到", v.Why);
            // 未贴边时另有一套措辞
            var far = AttentionRules.Evaluate(Position(zone: "below", atr: 1.2m, kind: null));
            Assert.Equal("wait", far.Level);
            Assert.Contains("尚未到位", far.Why);
        });

        t.Case("关注度_五条规则速查_完整且顺序稳定", () =>
        {
            Assert.Equal(5, AttentionRules.Rules.Length);
            Assert.Contains("只做三类位置", AttentionRules.Rules[1]);
            Assert.Contains("三不做", AttentionRules.Rules[4]);
        });
    }

    private static StructurePosition Position(
        string zone, decimal? atr, string? kind, int? bars = null, decimal? movedR = null, decimal? areaRatio = null) =>
        new(
            StrokeDirection: "up", StrokeConfirmed: true, StrokeFrom: 100m, StrokeTo: 110m,
            RetracePct: 0.4m, StrokeBars: 8,
            PivotZone: zone, PivotZg: 105m, PivotZd: 95m, PivotStrokes: 5, PivotBars: 40,
            EdgeDistancePct: atr is { } a ? a * 0.001m : null, EdgeDistanceAtr: atr,
            InvalidationPrice: 96m, StopReferencePrice: 97m,
            DistanceToStopReferencePct: 0.012m,
            LastKind: kind, BarsSinceLastSignal: kind is null ? null : bars ?? 1,
            LastSignalPrice: 100m, LastAreaRatio: areaRatio, MovedR: movedR,
            Levels: [], CrossLevel: "single", Summary: "");
}
