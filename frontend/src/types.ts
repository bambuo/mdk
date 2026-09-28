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
