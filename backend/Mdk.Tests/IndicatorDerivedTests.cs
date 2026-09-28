using Mdk.Api.Indicators;

namespace Mdk.Tests;

public static class AdxDmiTests
{
    public static void Register(TestKit t)
    {
        t.Case("Adx_单边上涨_收敛高位_且PlusDI占优", () =>
        {
            // 单边上涨：+DM 恒为 1，−DM 为 0 → +DI=50、−DI=0、DX=100 → ADX→100
            const int n = 60;
            var highs = new double[n];
            var lows = new double[n];
            var closes = new double[n];
            for (var i = 0; i < n; i++)
            {
                highs[i] = i + 2;
                lows[i] = i;
                closes[i] = i + 1;
            }

            var dmi = AdxDmi.Compute(highs, lows, closes, 14);

            Assert.Nan(dmi.PlusDi[13]);
            Assert.NotNan(dmi.PlusDi[14]);   // +DI 起点在下标 period
            Assert.Nan(dmi.Adx[26]);
            Assert.NotNan(dmi.Adx[27]);      // ADX 起点在下标 2·period−1

            Assert.Equal(50, dmi.PlusDi[^1], 6);
            Assert.Equal(0, dmi.MinusDi[^1], 6);
            Assert.True(dmi.Adx[^1] > 90, $"ADX 应收敛到 90 以上，实际 {dmi.Adx[^1]}");
        });

        t.Case("Adx_样本不足_全NaN", () =>
        {
            var dmi = AdxDmi.Compute([1, 2], [0, 1], [1, 1], 14);
            Assert.All(dmi.PlusDi, v => Assert.Nan(v));
            Assert.All(dmi.Adx, v => Assert.Nan(v));
        });
    }
}

public static class MacdTests
{
    public static void Register(TestKit t)
    {
        t.Case("Macd_常量序列_全为0", () =>
        {
            var closes = Enumerable.Repeat(100.0, 100).ToArray();
            var macd = Macd.Compute(closes);

            Assert.Nan(macd.Dif[24]);
            Assert.Equal(0, macd.Dif[25], 10);
            Assert.Equal(0, macd.Dea[^1], 10);
            Assert.Equal(0, macd.Hist[^1], 10);
        });

        t.Case("Macd_线性序列_DIF收敛到7倍斜率", () =>
        {
            // 线性序列上 EMA 滞后 = 斜率×(1−α)/α：α12 → 5.5，α26 → 12.5，DIF = 7×斜率
            var closes = Enumerable.Range(0, 200).Select(i => 100.0 + i).ToArray();
            var macd = Macd.Compute(closes);

            Assert.InRange(macd.Dif[150], 6.5, 7.5);
        });

        t.Case("Macd_有效起点位置", () =>
        {
            var closes = Enumerable.Range(0, 60).Select(i => 100.0 + i * 0.5).ToArray();
            var macd = Macd.Compute(closes);

            Assert.Nan(macd.Dif[24]);
            Assert.NotNan(macd.Dif[25]);   // slow−1
            Assert.Nan(macd.Dea[32]);
            Assert.NotNan(macd.Dea[33]);   // slow+signal−2
        });
    }
}

public static class BollingerBandsTests
{
    public static void Register(TestKit t)
    {
        t.Case("Boll_常量序列_三线重合", () =>
        {
            var closes = Enumerable.Repeat(42.0, 40).ToArray();
            var boll = BollingerBands.Compute(closes, 20, 2.0);
            Assert.Equal(42, boll.Upper[^1], 10);
            Assert.Equal(42, boll.Middle[^1], 10);
            Assert.Equal(42, boll.Lower[^1], 10);
        });

        t.Case("Boll_手算对照_均值与总体标准差", () =>
        {
            double[] closes = [1, 2, 3, 4, 5];
            var boll = BollingerBands.Compute(closes, 5, 2.0);

            Assert.Equal(3, boll.Middle[4], 10);
            // 总体标准差 σ = √2 → 上轨 = 3 + 2√2 ≈ 5.8284
            Assert.Equal(5.82842712474619, boll.Upper[4], 8);
            Assert.Equal(0.1715728752538097, boll.Lower[4], 8);
        });
    }
}
