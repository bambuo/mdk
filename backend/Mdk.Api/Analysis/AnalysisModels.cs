using Mdk.Api.Domain;
using Mdk.Api.Models;

namespace Mdk.Api.Analysis;

/// <summary>趋势方向判定结果。Direction：LONG=做多，SHORT=做空，RANGE=观望。</summary>
public sealed record TrendResult(string Direction, int Score, IReadOnlyList<string> Reasons);

/// <summary>
/// 关键价格位点。Kind：support=支撑（现价下方），resistance=阻力（现价上方）。
/// 强度口径见 <see cref="PriceLevels"/>：Score=Σ(触碰后的反应幅度，ATR 倍数)×时间衰减；
/// Touches=触碰次数；Sources=来源标签（摆动点/前日高/整数关口/日VWAP 等）。
/// </summary>
public sealed record PriceLevel(
    string Kind,
    decimal Price,
    decimal DistancePct,
    /// <summary>强度分（加权，见 <see cref="PriceLevels"/>）；纯客观锚点位点为 0（尚未被触碰）。</summary>
    decimal Score,
    /// <summary>触碰次数（簇内摆动极值个数）；0 表示该位点来自客观锚点、尚未被触碰。</summary>
    int Touches,
    /// <summary>平均反向反应幅度（ATR 倍数）；未被触碰时为 null。</summary>
    decimal? ReactionAtr,
    /// <summary>最近一次触碰距窗口末端的根数；未被触碰时为 null。</summary>
    int? AgeBars,
    /// <summary>是否被反复测试（触碰 ≥ 2 次）——已知战场；false 含"只见过一次"与"纯锚点"。</summary>
    bool Retested,
    /// <summary>来源标签（可多个）：摆动点 / 前日高 / 前日低 / 前周高 / 前周低 / 整数关口 / 日VWAP。</summary>
    IReadOnlyList<string> Sources,
    /// <summary>展示口径：强度分是否达到实线阈值（未达到画虚线）。</summary>
    bool Solid);

/// <summary>
/// 买卖点信号。Side：buy=买点（做多），sell=卖点（做空）。
/// IsConfirmed：true=基于已收盘K线确认（不可撤销）；false=盘中预警（基于未收盘K线，可能消失）。
/// Adx/AtrPct/BandwidthPct：信号发生时的市场状态（用于事后按状态统计，判断信号的状态依赖性）。
/// </summary>
public sealed record TradeSignal(
    long Time,
    string Side,
    string Source,
    decimal Price,
    string Note,
    decimal? StopPrice,
    bool IsConfirmed,
    decimal? Adx = null,
    decimal? AtrPct = null,
    decimal? BandwidthPct = null,
    /// <summary>级别共振标签：aligned=窗口内有同向高周期缠论信号，counter=只有反向，none=无（仅缠论信号有值）。</summary>
    string? Confluence = null,
    /// <summary>结构参考价：买卖点所依据的极值/中枢沿（仅缠论信号有值，用于衡量"入场是否已经追高"）。</summary>
    decimal? ReferencePrice = null,
    /// <summary>买卖点类别（"1买"/"2买"/"3买"/"1卖"/"2卖"/"3卖"）；非缠论信号为 null。</summary>
    string? Kind = null,
    /// <summary>联合打分 v2（按类别定义的等权 5 特征，见 JointScoreRules）；非缠论信号为 null。</summary>
    JointScoreDetail? JointScore = null)
{
    /// <summary>风险单位：入场到结构失效位（止损）的距离，占入场价比例。信号间可比的"1R"。</summary>
    public decimal? RiskPct => StopPrice is { } stop && Price != 0 ? Math.Abs(Price - stop) / Price : null;

    /// <summary>入场滞后：记账价相对结构参考价的偏离，占入场价比例（越大表示确认成本越高）。</summary>
    public decimal? EntryLagPct =>
        ReferencePrice is { } reference && Price != 0 ? Math.Abs(Price - reference) / Price : null;

    /// <summary>
    /// 滞后占比 = 入场滞后 ÷ 风险单位：衡量"该笔已完成的部分吃掉了多少风险额度"。
    /// 接近或超过 1 表示入场时价格已走完一个风险单位的距离（追高/追低，结构失效位离现价过近）。
    /// </summary>
    public decimal? LagShare => RiskPct is { } risk && risk > 0 && EntryLagPct is { } lag ? lag / risk : null;
}

// 联合打分明细（JointScoreDetail / JointScoreFeature）定义见 JointScoreRules.cs（v2，按类别定义特征）。

/// <summary>信号发生时的市场状态快照。</summary>
public readonly record struct SignalRegime(decimal? Adx, decimal? AtrPct, decimal? BandwidthPct);

/// <summary>已确认的历史摆动点（用于结构信号，避免使用未来数据）。</summary>
public readonly record struct SwingPoint(int Index, decimal Price, bool IsHigh);

public sealed record MacdSeries(decimal?[] Dif, decimal?[] Dea, decimal?[] Hist);

/// <summary>一个中枢的区间信息（供前端绘制中枢色带与列表）。</summary>
public sealed record ChanPivotInfo(
    long FromTime,
    long ToTime,
    decimal Zg,
    decimal Zd,
    int Strokes,
    bool IsConfirmed,
    /// <summary>是否落在某个高周期中枢内（多级别归属，仅本级别中枢有值）。</summary>
    bool? InsideHigher = null,
    /// <summary>内部包含的次级别中枢数量（仅本级别中枢有值）。</summary>
    int? LowerPivotCount = null);

/// <summary>某个周期的结构摘要（多级别叠加视图用）。</summary>
public sealed record ChanLevelStructure(
    /// <summary>higher=高周期 / primary=本级别 / lower=次级别</summary>
    string Role,
    string Interval,
    IReadOnlyList<ChanPivotInfo> Pivots,
    string LastStrokeDirection,
    int SignalCount,
    /// <summary>该级别数据的起始时间（数据不足以覆盖整个显示窗口时，前端据此提示）。</summary>
    long CoverageFromTime);

/// <summary>高周期（L1）结构上下文：用于"级别共振"展示（当前笔方向、价格相对最新中枢、最近买卖点）。</summary>
public sealed record ChanContextSummary(
    string Interval,
    string LastStrokeDirection,
    bool? PriceInPivot,
    decimal? PivotZg,
    decimal? PivotZd,
    string? LastKind,
    string? LastSide,
    long? LastTime,
    int SignalCount);

/// <summary>缠论结构摘要（供前端"缠论结构"卡片与信号标注使用）。</summary>
public sealed record ChanSummary(
    string LastStrokeDirection,
    bool LastStrokeConfirmed,
    int StrokeCount,
    int PivotCount,
    decimal? PivotZg,
    decimal? PivotZd,
    int PivotStrokes,
    bool? PriceInPivot,
    string? LastKind,
    long? LastTime,
    decimal? LastPrice,
    string? LastNote,
    IReadOnlyList<ChanPivotInfo> Pivots,
    /// <summary>高周期结构上下文（无高周期数据时为 null）。</summary>
    ChanContextSummary? HigherContext = null,
    /// <summary>
    /// 价格在结构中的位置（**确定性输出**，不含概率推断）：本笔方向与回撤位置、中枢归属与距离、
    /// 最近买卖点的结构失效位距离、多级别归属是否一致。回答"价格处在结构的哪里"，而非"该不该买"。
    /// </summary>
    StructurePosition? Position = null);

/// <summary>一次完整的分析结果（REST /api/analysis 与 WS analysis 推送共用此结构）。</summary>
public sealed record AnalysisResult(
    string Market,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Interval,
    long LastTime,
    decimal LastPrice,
    TrendResult Trend,
    IReadOnlyList<PriceLevel> Levels,
    IReadOnlyList<TradeSignal> Signals,
    IReadOnlyDictionary<string, decimal?[]> Series,
    MacdSeries Macd,
    ChanSummary? Chan = null,
    /// <summary>多级别结构（高周期 / 本级别 / 次级别）；无对应数据时该级别缺省。</summary>
    IReadOnlyList<ChanLevelStructure>? ChanLevels = null,
    /// <summary>
    /// 可信度表：按「来源 × 类别 × 周期」给出该语境在台账里的经验统计（样本量/胜率与区间/扣费后为正）。
    /// 前端按 (source, kind, interval) 查表给每个信号显示徽章；样本不足时只显示"样本不足"。
    /// </summary>
    IReadOnlyList<EvidenceBucket>? Evidence = null,
    /// <summary>位点的价格基础：last=最新成交价K线；mark=标记价K线（仅合约，用于避免插针造成的假位点）。</summary>
    string LevelsBasis = "last",
    /// <summary>杠杆面事实（资金费率/持仓量/基差/主动买占比）；现货只有主动买占比，缺失为 null。</summary>
    LeverageFacts? Leverage = null);

public sealed record KlinesResponse(
    string Market,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Interval,
    IReadOnlyList<Candle> Candles);

public sealed record SymbolQuote(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal LastPrice,
    decimal PriceChangePercent,
    decimal QuoteVolume,
    /// <summary>锚定币（稳定币/法币）：价格恒定，不应作默认分析标的，也不计入绩效统计。</summary>
    bool Pegged);

public sealed record Ticker24h(string Symbol, decimal LastPrice, decimal PriceChangePercent, decimal QuoteVolume);
