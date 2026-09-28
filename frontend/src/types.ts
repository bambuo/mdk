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
}

export interface MacdSeries {
  dif: (number | null)[]
  dea: (number | null)[]
  hist: (number | null)[]
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
}

/** 图表指标显示开关 */
export interface Toggles {
  ema: boolean
  rsi: boolean
  macd: boolean
  boll: boolean
  levels: boolean
  signals: boolean
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
}
