using Mdk.Api.Analysis;
using Mdk.Api.Models;

namespace Mdk.Tests;

/// <summary>
/// 成交量分布（POC / 价值区 / 高量与低量节点）与它作为锚点并入位点的口径。
/// 分箱均摊是**明示的近似**（真值需逐笔成交），故此处锁定的是口径自洽与边界，不外推精度。
/// </summary>
public static class VolumeProfileTests
{
    public static void Register(TestKit t)
    {
        t.Case("成交量分布_POC落在放量的价格上", () =>
        {
            // 前 10 根在 100 附近各成交 1，后 2 根在 105 附近各成交 100 → POC 应落在 105 一带
            var candles = new List<Candle>();
            for (var i = 0; i < 10; i++) candles.Add(Bar(1000 + i, 100m, 10m, 2m));
            for (var i = 0; i < 2; i++) candles.Add(Bar(1010 + i, 105m, 100m, 2m));

            var profile = VolumeProfile.Compute(candles, bins: 100);

            Assert.True(profile is not null, "应能算出成交量分布");
            Assert.InRange(profile!.Poc, 104m, 106m);
        });

        t.Case("成交量分布_价值区含主峰且占比不低于70%", () =>
        {
            // 80 根集中在 100（量 5），其余 20 根分散在 110（量 0.1）→ 价值区应窄且含 100
            var candles = new List<Candle>();
            for (var i = 0; i < 80; i++) candles.Add(Bar(1000 + i, 100m, 5m, 1m));
            for (var i = 0; i < 20; i++) candles.Add(Bar(1080 + i, 110m, 0.1m, 1m));

            var profile = VolumeProfile.Compute(candles, bins: 100)!;

            Assert.InRange(profile.Poc, 99m, 101m);
            Assert.True(profile.VaLow <= profile.Poc && profile.Poc <= profile.VaHigh, "价值区必须含 POC");
            var width = profile.VaHigh - profile.VaLow;
            var full = 111m - 99m;
            Assert.True(width < full * 0.5m, $"八成成交集中在 100，价值区不应覆盖大半区间（实际宽 {width:0.00}）");
        });

        t.Case("成交量分布_双峰之间识别为低量节点_峰为高量节点", () =>
        {
            var candles = new List<Candle>();
            for (var i = 0; i < 8; i++) candles.Add(Bar(1000 + i, 100m, 10m, 2m));      // 峰一
            for (var i = 0; i < 4; i++) candles.Add(Bar(1008 + i, 105m, 1m, 2m));       // 谷
            for (var i = 0; i < 8; i++) candles.Add(Bar(1012 + i, 110m, 10m, 2m));      // 峰二

            var profile = VolumeProfile.Compute(candles, bins: 100)!;

            Assert.Contains(profile.Hvn, p => Math.Abs(p - 100m) < 1.5m);
            Assert.Contains(profile.Hvn, p => Math.Abs(p - 110m) < 1.5m);   // 两个峰都报出（不被同一峰占满）
            Assert.True(profile.Lvn.Count > 0, "两峰之间应识别出低量节点");
            Assert.All(profile.Lvn, p => Assert.True(p > 101m && p < 109m, $"低量节点应落在两峰之间，实际 {p}"));
        });

        t.Case("成交量分布_数据不足或单一时返回空", () =>
        {
            Assert.True(VolumeProfile.Compute([], 100) is null);
            Assert.True(VolumeProfile.Compute([Bar(1000, 100m, 5m, 2m)], 100) is null, "不足 10 根不计算");
            var flat = Enumerable.Range(0, 20).Select(i => new Candle(1000 + i, 100m, 100m, 100m, 100m, 5m)).ToList();
            Assert.True(VolumeProfile.Compute(flat, 100) is null, "高低相同（无区间）不计算");
        });

        t.Case("成交量分布_锚点并入位点_重合时标注来源且不加分", () =>
        {
            // 摆动低点恰在整数关口 1000 上，再给一个成交量 POC 也落在同一价 → 三者合并
            var candles = new List<Candle>();
            for (var i = 0; i < 20; i++) candles.Add(Bar(1000 + i, 1040m, 10m, 1m));
            for (var i = 0; i < 5; i++) candles.Add(Bar(1020 + i, 1040m, 10m, 1m));
            candles.Add(Bar(1025, 1000m, 10m, 1m));                                   // 摆动低点
            for (var i = 0; i < 5; i++) candles.Add(Bar(1026 + i, 1040m, 10m, 1m));

            var withAnchor = PriceLevels.Analyze(candles,
                new PriceLevelOptions { Anchors = true },
                [new PriceAnchor(1000m, "成交量POC")]);
            var merged = withAnchor.FirstOrDefault(l => l.Sources.Contains("成交量POC"));

            Assert.True(merged is not null, "成交量锚点应出现在位点里");
            Assert.Contains(merged!.Sources, s => s == "摆动点");
            Assert.Equal(1, merged.Touches);
            Assert.True(merged.Score > 0, "合并后仍保留摆动点的强度分（锚点不加分也不减分）");
        });
    }

    /// <summary>一根K线：mid ± spread/2，指定成交量。</summary>
    private static Candle Bar(long index, decimal mid, decimal volume, decimal spread) =>
        new(1_700_000_000L + index * 60L, mid, mid + spread / 2, mid - spread / 2, mid, volume);
}
