namespace Mdk.Api.Analysis;

/// <summary>
/// 联合打分 v2（等权 0–5）——按**信号类别定义特征**，使特征语义与缠论语义一致（v1 的教训，见 PLAN §0.19）。
///
/// **预注册**（2026-09-30，规则与分档在跑数据前写定；判据同 §0.19：前 60% 选择 / 后 40% 验证，
/// 高分组独立波次 ≥30、超额 t ≥2.5、中位超额 &gt;0、扣费后为正 ≥50%、单一标的占比 ≤50%，且高分组 &gt; 低分组）：
///
/// · **1/2 类（反转语义）**：① 逆 EMA200（买在 EMA200 下方 / 卖在上方）② 强背驰（MACD 面积比 ≤0.7）
///   ③ RSI 极端（买 ≤35 / 卖 ≥65）④ 入场成本低（lagShare ≤0.5）⑤ 止损效率（riskPct ≤2.5%）
/// · **3 类（延续语义）**：① 顺 EMA200 ② RSI 顺向（买 ≥50 / 卖 ≤50）③ MACD 柱同向（买 ≥0 / 卖 ≤0）
///   ④ 入场成本低（lagShare ≤0.5）⑤ 止损效率（riskPct ≤2.5%）
///
/// 分档：低 ≤2 / 中 3 / 高 ≥4。
///
/// 纪律：得分高**不等于**可交易。v2 于未挖窗口检验仍未通过（PLAN §0.19），因此打分只作**事实聚合展示**；
/// "可实盘"档已废弃（判据依赖本质不确定的统计优势，见 SignalQualityRules 注释）。
/// </summary>
public static class JointScoreRules
{
    public const int MaxScore = 5;

    /// <summary>逐特征计算（在"缠论内部窗口"的指标序列上取 index 处；指标已降为图表，但仍是合法的语境数据）。</summary>
    public static JointScoreDetail Compute(
        decimal price, decimal? ema200, decimal? rsi, decimal? macdHist,
        string side, string? kind, decimal? areaRatio, decimal? riskPct, decimal? lagShare)
    {
        var features = kind is "1买" or "2买" or "1卖" or "2卖"
            ? ReversalFeatures(price, ema200, rsi, areaRatio, side == "buy", riskPct, lagShare)
            : ContinuationFeatures(price, ema200, rsi, macdHist, side == "buy", riskPct, lagShare);

        var score = features.Count(f => f.Hit);
        return new JointScoreDetail(score, MaxScore, kind, features);
    }

    /// <summary>1/2 类（反转语义）的 5 特征。</summary>
    private static JointScoreFeature[] ReversalFeatures(
        decimal price, decimal? ema200, decimal? rsi, decimal? areaRatio,
        bool isBuy, decimal? riskPct, decimal? lagShare)
    {
        return new[]
        {
            new JointScoreFeature("逆 EMA200（反转语境）",
                ema200 is { } e200 && (isBuy ? price < e200 : price > e200)),
            new JointScoreFeature("强背驰（面积比 ≤0.7）", areaRatio is { } ar && ar <= 0.7m),
            new JointScoreFeature("RSI 极端（买≤35 / 卖≥65）",
                rsi is { } rsiVal && (isBuy ? rsiVal <= 35m : rsiVal >= 65m)),
            new JointScoreFeature("入场成本低（≤0.5R）", lagShare is { } ls && ls <= 0.5m),
            new JointScoreFeature("止损效率（≤2.5%）", riskPct is { } rp && rp <= 0.025m),
        };
    }

    /// <summary>3 类（延续语义）的 5 特征。</summary>
    private static JointScoreFeature[] ContinuationFeatures(
        decimal price, decimal? ema200, decimal? rsi, decimal? macdHist,
        bool isBuy, decimal? riskPct, decimal? lagShare)
    {
        return new[]
        {
            new JointScoreFeature("顺 EMA200（延续语境）",
                ema200 is { } e200 && (isBuy ? price > e200 : price < e200)),
            new JointScoreFeature("RSI 顺向（买≥50 / 卖≤50）",
                rsi is { } rsiVal && (isBuy ? rsiVal >= 50m : rsiVal <= 50m)),
            new JointScoreFeature("MACD 柱同向（买≥0 / 卖≤0）",
                macdHist is { } hist && (isBuy ? hist >= 0m : hist <= 0m)),
            new JointScoreFeature("入场成本低（≤0.5R）", lagShare is { } ls && ls <= 0.5m),
            new JointScoreFeature("止损效率（≤2.5%）", riskPct is { } rp && rp <= 0.025m),
        };
    }

    /// <summary>滞后占比 = |入场价 − 结构参考价| ÷ |入场价 − 失效位|。</summary>
    public static decimal? LagShare(decimal price, decimal? reference, decimal? stop)
    {
        if (reference is not { } r || stop is not { } s) return null;
        var risk = Math.Abs(price - s);
        return risk <= 0m ? null : Math.Abs(price - r) / risk;
    }

    /// <summary>风险单位（占入场价比例）= |入场价 − 失效位| ÷ 入场价。</summary>
    public static decimal? RiskPct(decimal price, decimal? stop)
    {
        if (stop is not { } s || price <= 0m) return null;
        return Math.Abs(price - s) / price;
    }
}

/// <summary>单个打分特征（名称 + 是否命中）。</summary>
public sealed record JointScoreFeature(string Name, bool Hit);

/// <summary>联合打分明细：分数 + 该类别下的逐特征命中。</summary>
public sealed record JointScoreDetail(
    int Score,
    int MaxScore,
    /// <summary>信号类别（"1买"…"3卖"），特征按类别取反转或延续语义。</summary>
    string? Kind,
    IReadOnlyList<JointScoreFeature> Features);
