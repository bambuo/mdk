import type { AnalysisResult, KlinesResponse, MarketKind, SignalStatsResponse, SymbolQuote } from '../types'

async function getJson<T>(url: string): Promise<T> {
  const resp = await fetch(url)
  const body = (await resp.json().catch(() => null)) as { error?: string } | null
  if (!resp.ok) {
    const message = body && typeof body.error === 'string' ? body.error : `请求失败（HTTP ${resp.status}）`
    throw new Error(message)
  }
  return body as T
}

/** 交易对列表（按 24h 成交额排序，含交易币/计价币拆分；现货与合约各自拉取） */
export function fetchSymbols(market: MarketKind, quote = 'USDT'): Promise<SymbolQuote[]> {
  return getJson<SymbolQuote[]>(`/api/symbols?market=${market}&quote=${encodeURIComponent(quote)}&limit=80`)
}

export function fetchKlines(market: MarketKind, symbol: string, interval: string, limit = 500): Promise<KlinesResponse> {
  return getJson<KlinesResponse>(`/api/klines?market=${market}&symbol=${encodeURIComponent(symbol)}&interval=${interval}&limit=${limit}`)
}

export function fetchAnalysis(market: MarketKind, symbol: string, interval: string, limit = 500): Promise<AnalysisResult> {
  return getJson<AnalysisResult>(`/api/analysis?market=${market}&symbol=${encodeURIComponent(symbol)}&interval=${interval}&limit=${limit}`)
}

/** 信号历史绩效（事后评估；days 缺省 90） */
export function fetchSignalStats(market: MarketKind, symbol: string, interval: string, days = 90): Promise<SignalStatsResponse> {
  return getJson<SignalStatsResponse>(`/api/signal-stats?market=${market}&symbol=${encodeURIComponent(symbol)}&interval=${interval}&days=${days}`)
}
