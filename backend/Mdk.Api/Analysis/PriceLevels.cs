using Mdk.Api.Indicators;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>
/// 关键价格位点（支撑/阻力）的口径与旋钮。
/// 与缠论结构一样：位点在**固定内部窗口**上计算（<see cref="AnalysisBars"/>），与调用方的显示窗口解耦。
/// </summary>
public sealed class PriceLevelOptions
{
    public const string SectionName = "Levels";

    /// <summary>内部窗口根数（与显示窗口解耦，保证可复现）。</summary>
    public int AnalysisBars { get; set; } = 1000;

    /// <summary>摆动极值的左右确认根数（±N 根严格分形）。</summary>
    public int SwingLookback { get; set; } = 3;

    /// <summary>聚类容差（ATR(14) 的倍数）：容差内的极值视为同一位点。</summary>
    public decimal ClusterToleranceAtr { get; set; } = 0.5m;

    /// <summary>每侧最多展示的位点数（按距现价由近及远）。</summary>
    public int MaxPerSide { get; set; } = 5;

    /// <summary>衡量"触碰后的反应"所用的后续根数（自极值之后起算）。</summary>
    public int ReactionBars { get; set; } = 30;

    /// <summary>单次反应幅度上限（ATR 倍数），避免单一极端行情支配强度。</summary>
    public decimal ReactionCapAtr { get; set; } = 3m;

    /// <summary>强度的时间衰减半衰期（根）：越久远的触碰贡献越低。</summary>
    public decimal DecayHalfLifeBars { get; set; } = 250m;

    /// <summary>是否并入客观锚点（前日/前周高低、整数关口、日 VWAP）。</summary>
    public bool Anchors { get; set; } = true;

    /// <summary>合约是否改用标记价K线计算位点（防插针）：现货无标记价，始终用最新价。</summary>
    public bool UseMarkPriceForFutures { get; set; } = true;

    /// <summary>实线阈值（强度分）：低于此值的位点在图上画虚线。</summary>
    public decimal SolidScore { get; set; } = 2m;

    /// <summary>是否并入成交量分布锚点（POC / 价值区沿 / 高量与低量节点）。</summary>
    public bool UseVolumeProfile { get; set; } = true;

    /// <summary>成交量分布的统计天数（用 1 分钟K线，区间内成交量在K线区间均摊——近似，见 VolumeProfile）。</summary>
    public int ProfileDays { get; set; } = 14;

    /// <summary>成交量分布的分箱数（箱数越多越细，也越容易受噪音影响）。</summary>
    public int ProfileBins { get; set; } = 100;

    /// <summary>启动后预热成交量分布的延迟（秒）：避开启动时的取数高峰。</summary>
    public int ProfileWarmupDelaySeconds { get; set; } = 30;
}

/// <summary>外部锚点（成交量分布等）：与位点重合时**只标注来源、不加分**。</summary>
public readonly record struct PriceAnchor(decimal Price, string Label);

/// <summary>
/// 支撑/阻力位点（纯函数，可独立测试）。口径 v2（2026-10-01）：
///
///   ① **摆动极值**：±<see cref="PriceLevelOptions.SwingLookback"/> 根的严格分形
///      （等高的双顶/双底不产生极值——此处与缠论不同：缠论用包含处理化解平台，位点不处理，属已知取舍）
///   ② **每次触碰的反应**：极值之后 <see cref="PriceLevelOptions.ReactionBars"/> 根内的最大反向位移，
///      以该处的 ATR(14) 归一，上限 <see cref="PriceLevelOptions.ReactionCapAtr"/>
///   ③ **时间衰减**：权重 = 0.5^(距今根数 ÷ 半衰期)——越久远的触碰越不重要
///   ④ **强度分** Score = Σ(反应 × 权重)；容差内的极值并为一个位点，价位取均值，触碰数即成员数
///   ⑤ **客观锚点**：前日/前周高低、整数关口、日 VWAP——与邻近位点合并（标注来源）或独立成"未测试位点"
///   ⑥ **分侧**：以窗口末端收盘价分支撑/阻力，各取最近 N 个
///
/// 诚实边界：强度分是**展示用的启发式**，未经绩效检验（位点不产生信号、不进台账、不参与统计）。
/// 锚点重合**只标注、不加分**——"多因素重合更强"在本项目已被证伪过（指标共振无优势），
/// 未经检验的加权不得进入口径。
/// </summary>
public static class PriceLevels
{
    /// <summary>摆动极值检测（±lookback 根严格分形）。位点聚类与结构信号共用。</summary>
    public static IReadOnlyList<SwingPoint> FindSwings(IReadOnlyList<Candle> candles, int lookback = 3)
    {
        var swings = new List<SwingPoint>();
        for (var i = lookback; i < candles.Count - lookback; i++)
        {
            var isHigh = true;
            var isLow = true;
            for (var k = 1; k <= lookback; k++)
            {
                if (candles[i].High <= candles[i - k].High || candles[i].High <= candles[i + k].High) isHigh = false;
                if (candles[i].Low >= candles[i - k].Low || candles[i].Low >= candles[i + k].Low) isLow = false;
                if (!isHigh && !isLow) break;
            }

            if (isHigh) swings.Add(new SwingPoint(i, candles[i].High, true));
            if (isLow) swings.Add(new SwingPoint(i, candles[i].Low, false));
        }

        return swings;
    }

    public static IReadOnlyList<PriceLevel> Analyze(
        IReadOnlyList<Candle> candles,
        PriceLevelOptions options,
        IReadOnlyList<PriceAnchor>? externalAnchors = null)
    {
        if (candles.Count < 10) return [];
        var n = candles.Count;
        var last = candles[^1];
        var highs = candles.Select(c => c.High).ToArray();
        var lows = candles.Select(c => c.Low).ToArray();
        var atr = Atr.Compute(highs, lows, candles.Select(c => c.Close).ToArray(), 14);

        // 容差取窗口内最近的有效 ATR：口径是"以当前波动衡量位点宽度"，故不随各极值所处时点变化
        var tolerance = options.ClusterToleranceAtr * (LastValid(atr) ?? last.Close * 0.01m);

        var clusters = new List<Cluster>();
        foreach (var swing in FindSwings(candles, options.SwingLookback))
        {
            var reaction = Reaction(candles, highs, lows, atr, swing, options);
            var age = n - 1 - swing.Index;
            var weight = Decay(age, options.DecayHalfLifeBars);
            var cluster = clusters.FirstOrDefault(c => Math.Abs(swing.Price - c.Mean) <= tolerance);
            if (cluster is null) clusters.Add(new Cluster(swing, reaction, age, weight));
            else cluster.Add(swing, reaction, age, weight);
        }

        if (options.Anchors)
        {
            var anchors = Anchors(candles, last.Close)
                .Concat((externalAnchors ?? []).Select(a => (a.Price, a.Label)));
            foreach (var (price, label) in anchors)
            {
                var cluster = clusters.FirstOrDefault(c => Math.Abs(price - c.Mean) <= tolerance);
                if (cluster is null) clusters.Add(new Cluster(price, label));
                else cluster.AddLabel(label);
            }
        }

        var levels = clusters.Select(c => c.ToLevel(last.Close, options.SolidScore)).ToList();
        var resistances = levels.Where(l => l.Kind == "resistance").OrderBy(l => l.Price).Take(options.MaxPerSide);
        var supports = levels.Where(l => l.Kind == "support").OrderByDescending(l => l.Price).Take(options.MaxPerSide);
        return [.. resistances, .. supports];
    }

    /// <summary>
    /// 极值之后的"反应幅度"：低点看到的最大上涨、高点看到的最大下跌（ATR 倍数，0~上限）。
    /// 极值后价格继续朝原方向走（位点被击穿）则反应≈0——这正是"脆弱位点"的表达。
    /// 距窗口末端不足 ReactionBars 根的触碰只能量到已有部分（会低估，属诚实截断）。
    /// </summary>
    private static decimal? Reaction(
        IReadOnlyList<Candle> candles, decimal[] highs, decimal[] lows, decimal?[] atr,
        SwingPoint swing, PriceLevelOptions options)
    {
        // ATR 尚在预热期的极值不参与强度（避免用未来值归一）
        if (swing.Index >= atr.Length || atr[swing.Index] is not { } unit || unit <= 0) return null;
        var from = swing.Index + 1;
        if (from >= candles.Count) return 0m;
        var to = Math.Min(candles.Count, swing.Index + options.ReactionBars + 1);
        var extreme = swing.IsHigh ? lows[from..to].Min() : highs[from..to].Max();
        var move = swing.IsHigh ? swing.Price - extreme : extreme - swing.Price;
        return Math.Clamp(move / unit, 0m, options.ReactionCapAtr);
    }

    /// <summary>时间衰减权重（半衰期口径；浮点仅用于指数，结果回 decimal）。</summary>
    private static decimal Decay(int ageBars, decimal halfLifeBars)
    {
        if (halfLifeBars <= 0) return 1m;
        return (decimal)Math.Pow(0.5, (double)(ageBars / halfLifeBars));
    }

    /// <summary>
    /// 客观锚点：前日高低、前周高低（周一 00:00 UTC 为界，均取**已收盘**的那一段）、
    /// 现价上下的最近整数关口、当日 VWAP（自 UTC 00:00 起累计）。
    /// 周期过长时对应锚点无意义（周线上的"前日高低"），按每根K线跨度自动略过。
    /// </summary>
    private static IEnumerable<(decimal Price, string Label)> Anchors(IReadOnlyList<Candle> candles, decimal price)
    {
        const long dayMs = 86_400_000L;
        var n = candles.Count;
        var endMs = candles[^1].Time * 1000L;
        var barMs = n > 1 ? Math.Max(1L, (candles[^1].Time - candles[0].Time) * 1000L / (n - 1)) : dayMs;
        var todayMs = endMs / dayMs * dayMs;

        if (barMs < dayMs)
        {
            var prevDay = Between(candles, todayMs - dayMs, todayMs);
            if (prevDay.Count > 0)
            {
                yield return (prevDay.Max(c => c.High), "前日高");
                yield return (prevDay.Min(c => c.Low), "前日低");
            }
        }

        if (barMs < 7 * dayMs)
        {
            var days = endMs / dayMs;
            var weekMs = (days - (days + 3) % 7) * dayMs; // (days+3)%7：epoch 为周四 → 周一为界
            var prevWeek = Between(candles, weekMs - 7 * dayMs, weekMs);
            if (prevWeek.Count > 0)
            {
                yield return (prevWeek.Max(c => c.High), "前周高");
                yield return (prevWeek.Min(c => c.Low), "前周低");
            }
        }

        var step = RoundStep(price);
        if (step > 0)
        {
            var up = (Math.Floor(price / step) + 1) * step;
            var down = (Math.Ceiling(price / step) - 1) * step;
            if (up > price) yield return (up, "整数关口");
            if (down > 0 && down < price) yield return (down, "整数关口");
        }

        var today = Between(candles, todayMs, long.MaxValue);
        var volume = today.Sum(c => c.Volume);
        if (volume > 0)
        {
            var typical = today.Sum(c => (c.High + c.Low + c.Close) / 3m * c.Volume);
            yield return (typical / volume, "日VWAP");
        }
    }

    private static List<Candle> Between(IReadOnlyList<Candle> candles, long fromMs, long toMs) =>
        candles.Where(c => c.Time * 1000L >= fromMs && c.Time * 1000L < toMs).ToList();

    /// <summary>整数关口步长：10^(数量级−1)，即每十倍价格区间约 10 个关口（BTC 83,000 → 步长 1,000）。</summary>
    private static decimal RoundStep(decimal price)
    {
        if (price <= 0) return 0m;
        var magnitude = Math.Floor(Math.Log10((double)price)); // 仅取数量级，浮点误差不影响
        return (decimal)Math.Pow(10, magnitude - 1);
    }

    private static decimal Pct(decimal level, decimal price) => Math.Round((level - price) / price * 100, 2);

    private static decimal? LastValid(decimal?[] values)
    {
        for (var i = values.Length - 1; i >= 0; i--)
            if (values[i] is not null)
                return values[i];
        return null;
    }

    /// <summary>位点聚类（时序贪心：并入第一个均值在容差内的簇；簇均值即价位）。</summary>
    private sealed class Cluster
    {
        private decimal _sum;
        private int _count;
        private decimal _reactionSum;
        private decimal _score;
        private int _minAge;

        /// <summary>纯锚点（无摆动极值成员）的价位；有成员时以成员均值为准。</summary>
        private decimal _anchorPrice;

        public Cluster(SwingPoint swing, decimal? reaction, int age, decimal weight)
        {
            _sum = swing.Price;
            _count = 1;
            _reactionSum = reaction ?? 0m;
            _score = (reaction ?? 0m) * weight;
            _minAge = age;
            Labels = [SwingLabel];
        }

        /// <summary>纯锚点（附近没有摆动极值）：未测试位点。</summary>
        public Cluster(decimal price, string label)
        {
            _anchorPrice = price;
            _count = 0;
            _minAge = -1;
            Labels = [label];
        }

        public List<string> Labels { get; }

        public decimal Mean => _count == 0 ? _anchorPrice : _sum / _count;

        public void Add(SwingPoint swing, decimal? reaction, int age, decimal weight)
        {
            _sum += swing.Price;
            _count++;
            _reactionSum += reaction ?? 0m;
            _score += (reaction ?? 0m) * weight;
            _minAge = Math.Min(_minAge, age);
        }

        public void AddLabel(string label)
        {
            if (!Labels.Contains(label, StringComparer.Ordinal)) Labels.Add(label);
        }

        public PriceLevel ToLevel(decimal lastClose, decimal solidScore)
        {
            var score = Math.Round(_score, 2);
            return new PriceLevel(
                Kind: Mean < lastClose ? "support" : "resistance",
                Price: Math.Round(Mean, 8),
                DistancePct: Pct(Mean, lastClose),
                Score: score,
                Touches: _count,
                ReactionAtr: _count == 0 ? null : Math.Round(_reactionSum / _count, 2),
                AgeBars: _count == 0 ? null : _minAge,
                Retested: _count >= 2,
                Sources: Labels,
                Solid: score >= solidScore);
        }

        private const string SwingLabel = "摆动点";
    }
}
