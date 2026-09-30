import type {
  AnalysisResult, KlinesResponse, MarketKind, SignalStatsResponse, SymbolQuote,
  WatchlistItemView, WatchlistSignalView,
} from '../types'

async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
  const resp = await fetch(url, init)
  const body = (await resp.json().catch(() => null)) as { error?: string } | null
  if (!resp.ok) {
    const message = body && typeof body.error === 'string' ? body.error : `请求失败（HTTP ${resp.status}）`
    throw new Error(message)
  }
  return body as T
}

function getJson<T>(url: string): Promise<T> {
  return requestJson<T>(url)
}

function sendJson<T>(url: string, method: string, payload?: unknown): Promise<T> {
  return requestJson<T>(url, {
    method,
    headers: { 'content-type': 'application/json' },
    body: payload === undefined ? undefined : JSON.stringify(payload),
  })
}

/** 交易对列表（按 24h 成交额排序，含交易币/计价币拆分；现货与合约各自拉取） */
export function fetchSymbols(market: MarketKind, quote = 'USDT'): Promise<SymbolQuote[]> {
  return getJson<SymbolQuote[]>(`/api/symbols?market=${market}&quote=${encodeURIComponent(quote)}&limit=80`)
}

export function fetchKlines(market: MarketKind, symbol: string, interval: string, limit = 500): Promise<KlinesResponse> {
  return getJson<KlinesResponse>(`/api/klines?market=${market}&symbol=${encodeURIComponent(symbol)}&interval=${interval}&limit=${limit}`)
}

export function fetchAnalysis(
  market: MarketKind, symbol: string, interval: string, limit = 500,
): Promise<AnalysisResult> {
  return getJson<AnalysisResult>(
    `/api/analysis?market=${market}&symbol=${encodeURIComponent(symbol)}&interval=${interval}&limit=${limit}`,
  )
}

/** 信号历史绩效（事后评估；days 缺省 90） */
export function fetchSignalStats(market: MarketKind, symbol: string, interval: string, days = 90): Promise<SignalStatsResponse> {
  return getJson<SignalStatsResponse>(`/api/signal-stats?market=${market}&symbol=${encodeURIComponent(symbol)}&interval=${interval}&days=${days}`)
}

/** ── 监控列表 ── */

export function fetchWatchlist(): Promise<WatchlistItemView[]> {
  return getJson<WatchlistItemView[]>('/api/watchlist')
}

export function addWatchlist(market: MarketKind, symbol: string, intervals: string[]): Promise<unknown> {
  return sendJson('/api/watchlist', 'POST', { market, symbol, intervals })
}

export function removeWatchlist(market: MarketKind, symbol: string): Promise<unknown> {
  return sendJson(`/api/watchlist?market=${market}&symbol=${encodeURIComponent(symbol)}`, 'DELETE')
}

export function toggleWatchlist(market: MarketKind, symbol: string): Promise<unknown> {
  return sendJson(`/api/watchlist/toggle?market=${market}&symbol=${encodeURIComponent(symbol)}`, 'POST')
}

export function fetchWatchlistSignals(limit = 50): Promise<WatchlistSignalView[]> {
  return getJson<WatchlistSignalView[]>(`/api/watchlist/signals?limit=${limit}`)
}
