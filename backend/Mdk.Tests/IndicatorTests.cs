using Mdk.Api.Indicators;

namespace Mdk.Tests;

/// <summary>
/// Wilder 平滑与均线的手算对照。
/// RMA 递推：种子 = 前 n 个的 SMA，之后 MA = (MA×(n−1) + x)/n。
/// </summary>
public static class WilderSmoothingTests
{
    public static void Register(TestKit t)
    {
        t.Case("Rma_种子为SMA_其后按Wilder递推", () =>
        {
            decimal[] values = [2, 4, 6, 8, 10];
            var result = WilderSmoothing.Rma(values, 3);

            Assert.Nan(result[0]);
            Assert.Nan(result[1]);
            Assert.Equal(4, result[2], 10);                    // (2+4+6)/3
            Assert.Equal(5.333333333333333m, result[3], 10);    // 4×2/3 + 8/3
            Assert.Equal(6.888888888888889m, result[4], 10);    // 5.3333m×2/3 + 10/3
        });

        t.Case("Rma_样本不足_全NaN", () =>
        {
            var result = WilderSmoothing.Rma([1, 2, 3], 5);
            Assert.All(result, v => Assert.Nan(v));
        });

        t.Case("Rma_常量序列_收敛到常量", () =>
        {
            var result = WilderSmoothing.Rma(Enumerable.Repeat(7.0m, 50).ToArray(), 14);
            Assert.Equal(7, result[^1], 10);
        });

        t.Case("Sma_滑动窗口", () =>
        {
            decimal[] values = [1, 2, 3, 4, 5];
            var result = Sma.Compute(values, 3);
            Assert.Nan(result[1]);
            Assert.Equal(2, result[2], 10);
            Assert.Equal(3, result[3], 10);
            Assert.Equal(4, result[4], 10);
        });

        t.Case("Ema_种子SMA_后按α递推", () =>
        {
            decimal[] values = [10, 10, 10, 20];
            var result = Ema.Compute(values, 3);

            Assert.Equal(10, result[2], 10);   // 种子 SMA
            Assert.Equal(15, result[3], 10);   // α=0.5m → 10 + 0.5m×(20−10)
        });
    }
}

public static class RsiTests
{
    public static void Register(TestKit t)
    {
        t.Case("Rsi_单边上涨_为100", () =>
        {
            var closes = Enumerable.Range(1, 30).Select(i => (decimal)i).ToArray();
            var rsi = Rsi.Compute(closes, 14);
            Assert.Nan(rsi[13]);
            Assert.Equal(100, rsi[14], 10);
            Assert.Equal(100, rsi[^1], 10);
        });

        t.Case("Rsi_交替涨跌_手算对照", () =>
        {
            // 10,11,10,11,…（15 个点），最后一个点改为 9 → 最后一个差值为 −1
            var closes = new decimal[16];
            for (var i = 0; i < 15; i++) closes[i] = i % 2 == 0 ? 10 : 11;
            closes[15] = 9;
            var rsi = Rsi.Compute(closes, 14);

            Assert.Equal(50, rsi[14], 10);
            // avgGain=(0.5m×13)/14, avgLoss=(0.5m×13+1)/14 → RSI≈46.4286m
            Assert.Equal(46.42857142857143m, rsi[15], 6);
        });

        t.Case("Rsi_样本不足_全NaN", () =>
        {
            var rsi = Rsi.Compute(Enumerable.Range(1, 10).Select(i => (decimal)i).ToArray(), 14);
            Assert.All(rsi, v => Assert.Nan(v));
        });
    }
}

public static class AtrTests
{
    public static void Register(TestKit t)
    {
        t.Case("Atr_手算对照", () =>
        {
            decimal[] highs = [10, 10.5m, 11, 12];
            decimal[] lows = [9, 9.5m, 10, 10.8m];
            decimal[] closes = [9.5m, 10, 10.5m, 11.5m];
            var atr = Atr.Compute(highs, lows, closes, 3);

            Assert.Nan(atr[1]);
            Assert.Equal(1, atr[2], 10);                    // TR=(1,1,1) → 种子 1
            Assert.Equal(1.1666666666666667m, atr[3], 10);   // (1×2 + 1.5m)/3
        });

        t.Case("Atr_真实波幅使用前收盘", () =>
        {
            // TR[1] = max(H−L, |H−Cprev|, |L−Cprev|) = max(1.5m, 1.5m, 0) = 1.5m
            decimal[] highs = [10, 11];
            decimal[] lows = [9, 9.5m];
            decimal[] closes = [9.5m, 10];
            var tr = Atr.Compute(highs, lows, closes, 1);
            Assert.Equal(1, tr[0], 10);    // 首根 H−L
            Assert.Equal(1.5m, tr[1], 10);  // |H1−C0|
        });
    }
}
