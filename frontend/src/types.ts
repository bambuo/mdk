/** 与后端 JSON（camelCase）一一对应的类型定义 */

/** 市场类型：现货 / U 本位合约 */
export type MarketKind = 'spot' | 'futures'

export interface Candle {
  time: number
  open: number
  high: number
  low: number
  close: number
  volume: number
}

export interface KlinesResponse {
  market: MarketKind
  symbol: string
  baseAsset: string
  quoteAsset: string
  interval: string
  candles: Candle[]
}

export interface SymbolQuote {
  symbol: string
  baseAsset: string
  quoteAsset: string
  lastPrice: number
  priceChangePercent: number
  quoteVolume: number
  /** 锚定币（稳定币/法币）：价格恒定，不作默认分析标的。规则由后端领域层给出（Domain/PeggedAssets.cs） */
  pegged: boolean
}

/** 台账里同语境（来源 × 类别 × 周期）的经验表现——界面徽章只陈述证据，不给"置信度分数" */
export interface CredibilityBucket {
  source: string
  kind: string | null
  interval: string
  /** 原始样本数 */
  n: number
  /** 独立波次（统计显著性的有效样本量） */
  nEpisodes: number
  /** 波次是否达到可给出胜率的最低要求（不足时界面必须显示"样本不足"） */
  sufficient: boolean
  winRate: number
  winRateLow: number
  winRateHigh: number
  netPositiveRate: number
  medianExcess: number
  medianRiskPct: number
}

export interface TrendResult {
  direction: 'LONG' | 'SHORT' | 'RANGE'
  score: number
  reasons: string[]
}

export interface PriceLevel {
  kind: 'support' | 'resistance'
  price: number
  strength: number
  distancePct: number
}

export interface TradeSignal {
  time: number
  side: 'buy' | 'sell'
  source: string
  price: number
  note: string
  stopPrice: number | null
  /** true=已收盘确认（不可撤销）；false=盘中预警（可能消失） */
  isConfirmed: boolean
  /** 信号方向与高一期 EMA50 是否同向（多周期共振）；高周期数据缺失时为 null */
  trendAligned: boolean | null
  /** 信号发生时的市场状态（ADX 强度 / ATR 占价比例 / 布林带宽比例） */
  adx: number | null
  atrPct: number | null
  bandwidthPct: number | null
  /** 级别共振标签：aligned=窗口内有同向高周期缠论信号；counter=只有反向；none=无（仅缠论信号有值） */
  confluence: 'aligned' | 'counter' | 'none' | null
  /** 买卖点类别（"3买"…）；非缠论信号为 null */
  kind?: string | null
  /** 结构参考价（买卖点所依据的极值/中枢沿）：用于判断入场是否已经追高 */
  referencePrice?: number | null
  /** 风险单位：入场到结构失效位的距离占入场价比例（信号间可比的 1R） */
  riskPct?: number | null
  /** 入场滞后：记账价相对结构参考价的偏离占入场价比例 */
  entryLagPct?: number | null
  /** 滞后占比 = 入场滞后 ÷ 风险单位：接近 1 表示确认成本已吃掉一个风险单位 */
  lagShare?: number | null
}

export interface MacdSeries {
  dif: (number | null)[]
  dea: (number | null)[]
  hist: (number | null)[]
}

/** 中枢区间（用于绘制色带与列表） */
export interface ChanPivotInfo {
  fromTime: number
  toTime: number
  zg: number
  zd: number
  strokes: number
  isConfirmed: boolean
  /** 是否落在高周期中枢内（仅本级别中枢有值） */
  insideHigher?: boolean | null
  /** 该中枢运行期间出现的次级别中枢数量（仅本级别中枢有值） */
  lowerPivotCount?: number | null
}

/** 多级别结构（高周期 / 本级别 / 次级别） */
export interface ChanLevelStructure {
  role: 'higher' | 'primary' | 'lower'
  interval: string
  pivots: ChanPivotInfo[]
  lastStrokeDirection: 'up' | 'down' | 'none'
  signalCount: number
  coverageFromTime: number
}

/** 缠论结构摘要 */
export interface ChanSummary {
  lastStrokeDirection: 'up' | 'down' | 'none'
  lastStrokeConfirmed: boolean
  strokeCount: number
  pivotCount: number
  pivotZg: number | null
  pivotZd: number | null
  pivotStrokes: number
  priceInPivot: boolean | null
  lastKind: string | null
  lastTime: number | null
  lastPrice: number | null
  lastNote: string | null
  pivots: ChanPivotInfo[]
  /** 结构模式：stroke=笔中枢（默认）/ segment=线段中枢 */
  levelMode: 'stroke' | 'segment'
  /** 线段数量（笔模式下为 0） */
  segmentCount: number
  /** 高周期结构上下文（级别共振的依据） */
  higherContext: {
    interval: string
    lastStrokeDirection: 'up' | 'down' | 'none'
    priceInPivot: boolean | null
    pivotZg: number | null
    pivotZd: number | null
    lastKind: string | null
    lastSide: 'buy' | 'sell' | null
    lastTime: number | null
    signalCount: number
  } | null
}

export interface AnalysisResult {
  market: MarketKind
  symbol: string
  baseAsset: string
  quoteAsset: string
  interval: string
  lastTime: number
  lastPrice: number
  trend: TrendResult
  levels: PriceLevel[]
  signals: TradeSignal[]
  series: Record<string, (number | null)[]>
  macd: MacdSeries
  chan: ChanSummary | null
  chanLevels: ChanLevelStructure[] | null
  /** 可信度表（按 来源 × 类别 分组）：界面据此给每个信号显示经验表现徽章 */
  credibility?: CredibilityBucket[] | null
}

/** 图表指标显示开关 */
export interface Toggles {
  ema: boolean
  rsi: boolean
  macd: boolean
  boll: boolean
  levels: boolean
  signals: boolean
  /** 缠论图层：笔折线 + 中枢带 + 分型点 */
  chan: boolean
  /** 多级别叠加：同时显示高周期与次级别中枢（宽/细色带） */
  multiLevel: boolean
  /** 线段模式：以线段构建中枢与买卖点（完整缠论体系）；关闭则用笔中枢 */
  segments: boolean
}

/** 单来源信号的历史绩效（事后评估，扣费口径见后端） */
export interface SignalSourceStats {
  source: string
  n: number
  /** 波次口径：同一波段内的重复信号只算一个有效样本 */
  nEpisodes: number
  winRate: number
  avgReturn: number
  avgExcess: number
  netPositiveRate: number
  stopHitRate: number
  /** 准入分级：可参考 / 仅观察 / 不达标 / 样本不足 */
  grade: string
  /** 分级依据（不达标时说明具体原因，如 "t=1.66 < 2"） */
  gradeReason: string
  /** 集中度：样本最多的单一标的占比 */
  topSymbolShare: number
}

/** 按级别共振标签分组的绩效 */
export interface SignalBucketStatsMulti {
  label: string
  n: number
  winRate: number
  avgExcess: number
  netPositiveRate: number
}

/** 分市场状态或分共振状态的分组绩效 */
export interface SignalBucketStats {
  label: string
  n: number
  winRate: number
  avgExcess: number
  netPositiveRate: number
}

export interface SignalStatsResponse {
  totalEvaluated: number
  overall: SignalSourceStats | null
  bySource: SignalSourceStats[]
  byRegime: SignalBucketStats[]
  byAlignment: SignalBucketStats[]
  byConfluence: SignalBucketStats[]
}
