using Mdk.Api.Analysis.Chan;

namespace Mdk.Tests;

/// <summary>级别共振标签：窗口内同向/反向/无信号，边界与无未来函数。</summary>
public static class ConfluenceTaggerTests
{
    private static ChanBuySellPoint P(long time, string side) =>
        new(Kind: side == "buy" ? "1买" : "1卖", Side: side, Time: time, Price: 100, ReferencePrice: 99,
            ReferenceBarIndex: 0, StopPrice: 98, AreaRatio: null, Note: "");

    public static void Register(TestKit t)
    {
        t.Case("共振标签_窗口内有同向高周期信号则标记共振", () =>
        {
            var htf = new[] { P(1000, "buy"), P(3000, "sell") };
            var tags = ConfluenceTagger.Tag(htf, windowSeconds: 500, [(1000, "buy"), (1200, "buy")]);
            Assert.Equal(ConfluenceTagger.Aligned, tags[(1000, "buy")]);
            Assert.Equal(ConfluenceTagger.Aligned, tags[(1200, "buy")]);
        });

        t.Case("共振标签_只有反向信号则标记逆向共振", () =>
        {
            var htf = new[] { P(1000, "sell") };
            var tags = ConfluenceTagger.Tag(htf, windowSeconds: 500, [(1200, "buy")]);
            Assert.Equal(ConfluenceTagger.Counter, tags[(1200, "buy")]);
        });

        t.Case("共振标签_窗口内无高周期信号则标记无共振", () =>
        {
            var htf = new[] { P(100, "buy") };
            var tags = ConfluenceTagger.Tag(htf, windowSeconds: 500, [(2000, "buy")]);
            Assert.Equal(ConfluenceTagger.None, tags[(2000, "buy")]);
        });

        t.Case("共振标签_窗口边界_恰好落在起点算入_超出则不算", () =>
        {
            var htf = new[] { P(1500, "buy") };
            // 本级别信号在 2000，窗口 500 → 覆盖 [1500, 2000]，1500 恰好算入
            Assert.Equal(ConfluenceTagger.Aligned, ConfluenceTagger.Tag(htf, 500, [(2000, "buy")])[(2000, "buy")]);
            // 窗口 499 → 覆盖 [1501, 2000]，1500 超出
            Assert.Equal(ConfluenceTagger.None, ConfluenceTagger.Tag(htf, 499, [(2000, "buy")])[(2000, "buy")]);
        });

        t.Case("共振标签_无未来函数_晚于本级别信号的高周期信号不计入", () =>
        {
            var htf = new[] { P(2001, "buy") };   // 比本级别信号晚 1 秒
            var tags = ConfluenceTagger.Tag(htf, windowSeconds: 5000, [(2000, "buy")]);
            Assert.Equal(ConfluenceTagger.None, tags[(2000, "buy")],
                "高周期信号若晚于本级别信号，不得用于共振（否则构成未来函数）");
        });

        t.Case("共振标签_同向优先于反向", () =>
        {
            var htf = new[] { P(1000, "sell"), P(1100, "buy") };
            var tags = ConfluenceTagger.Tag(htf, 500, [(1200, "buy")]);
            Assert.Equal(ConfluenceTagger.Aligned, tags[(1200, "buy")], "同向与反向同时存在时应判为共振");
        });

        t.Case("共振标签_中文展示", () =>
        {
            Assert.Equal("共振", ConfluenceTagger.Label(ConfluenceTagger.Aligned));
            Assert.Equal("逆向共振", ConfluenceTagger.Label(ConfluenceTagger.Counter));
            Assert.Equal("无共振", ConfluenceTagger.Label(ConfluenceTagger.None));
            Assert.Equal("无共振", ConfluenceTagger.Label(null));
        });
    }
}
