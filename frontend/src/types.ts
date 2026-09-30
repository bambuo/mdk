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

/** 台账里同语境（类别 × 周期）的经验表现——界面徽章只陈述证据，不给"置信度分数" */
export interface CredibilityBucket {
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
  /** 联合打分 v2（按信号类别定义的等权 5 特征，见后端 JointScoreRules）；非缠论信号为 null。
   *  v1 预注册检验未通过（顺势特征与反转语义相抵）；v2 重新预注册，检验结果见 PLAN §0.19。
   *  打分不是交易授权——"可实盘"只由实盘晋升判据决定。 */
  jointScore?: {
    score: number
    maxScore: number
    kind: string | null
    features: { name: string; hit: boolean }[]
  } | null
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
  /** 准入分级（低→高）：样本不足 / 仅观察 / 可参考 / 可实盘（实盘晋升判据全部达标才出现） */
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
  /** 统计窗口口径（按记录时间；回填样本的记录时间=回填运行时刻） */
  windowBasis: string
  /** 窗口内独立波次数（实盘判据的样本量） */
  windowEpisodes: number
  /** 其中实时落库的独立波次数（实盘判据要求 ≥ realtimeRequired） */
  realtimeEpisodes: number
  /** 实盘判据要求的实时波次门槛 */
  realtimeRequired: number
  totalEvaluated: number
  overall: SignalSourceStats | null
  bySource: SignalSourceStats[]
  byRegime: SignalBucketStats[]
  byConfluence: SignalBucketStats[]
}

/** ── 监控页 ── */

/** 单个周期的结构快照（后台最新一次分析的摘要） */
export interface WatchlistIntervalSnapshot {
  interval: string
  lastPrice: number
  lastTime: number
  strokeDirection: 'up' | 'down' | 'none'
  strokeConfirmed: boolean
  pivotZg: number | null
  pivotZd: number | null
  pivotStrokes: number
  priceInPivot: boolean | null
  lastKind: string | null
  lastSide: 'buy' | 'sell' | null
  lastSignalTime: number | null
  /** 最近买卖点距最新K线的根数（null = 无信号 / 无法计算） */
  barsSinceSignal: number | null
  signalCount: number
  /** 各指标当前状态（EMA 排列 / RSI 位置 / MACD 动能；指标已降为纯图表，不产生信号） */
  indicatorStates: { source: string; state: string; tone: 'bull' | 'bear' | 'neutral' }[]
  updatedAt: number
}

/** 一个监控条目（用户指定并持久化） */
export interface WatchlistItemView {
  market: MarketKind
  symbol: string
  baseAsset: string
  quoteAsset: string
  intervals: string[]
  enabled: boolean
  createdAt: number
  snapshots: WatchlistIntervalSnapshot[]
  /** 跨周期共振：aligned=同向 / mixed=多空分歧 / none=无 / pending=尚无数据 */
  resonance: 'aligned' | 'mixed' | 'none' | 'pending'
}

/** 监控列表内的最近信号（全部来源；缠论行带可信度，指标信号不评级） */
export interface WatchlistSignalView {
  market: MarketKind
  symbol: string
  baseAsset: string
  quoteAsset: string
  interval: string
  source: string
  kind: string | null
  side: 'buy' | 'sell'
  time: number
  price: number
  stopPrice: number | null
  note: string | null
  confluence: string | null
  isConfirmed: boolean
  recordedAt: number
  credibility: CredibilityBucket | null
}
