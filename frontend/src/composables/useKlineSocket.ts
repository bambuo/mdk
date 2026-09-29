import { onBeforeUnmount, ref, watch, type Ref } from 'vue'
import type { AnalysisResult, Candle, MarketKind } from '../types'

export type SocketStatus = 'connecting' | 'open' | 'closed'

export interface KlineHandlers {
  onKline: (candle: Candle) => void
  onAnalysis: (analysis: AnalysisResult) => void
}

/**
 * 后端 /ws 实时链路：K线增量 + 分析结果推送。
 * 市场/币种/周期变化时重连；断线指数退避（1s→15s）自动重连；
 * 通过代数（generation）丢弃旧连接的迟到消息。
 */
export function useKlineSocket(
  market: Ref<MarketKind>,
  symbol: Ref<string>,
  interval: Ref<string>,
  segments: Ref<boolean>,
  handlers: KlineHandlers,
) {
  const status = ref<SocketStatus>('closed')
  let ws: WebSocket | null = null
  let generation = 0
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null
  let reconnectDelay = 1000

  function disconnect() {
    generation++
    if (reconnectTimer) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
    if (ws) {
      ws.onopen = null
      ws.onmessage = null
      ws.onerror = null
      ws.onclose = null
      try {
        ws.close()
      } catch {
        // 已断开
      }
      ws = null
    }
  }

  function connect() {
    disconnect()
    const gen = generation
    const mkt = market.value
    const sym = symbol.value
    const itv = interval.value
    status.value = 'connecting'
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:'
    const socket = new WebSocket(`${proto}//${location.host}/ws?market=${mkt}&symbol=${sym}&interval=${itv}&segments=${segments.value}`)
    ws = socket

    socket.onopen = () => {
      if (gen !== generation) return
      status.value = 'open'
      reconnectDelay = 1000
    }

    socket.onmessage = (ev) => {
      if (gen !== generation) return
      let msg: { type: string; data: Record<string, unknown> }
      try {
        msg = JSON.parse(ev.data as string)
      } catch {
        return
      }
      if (msg.type === 'kline') {
        const d = msg.data
        if (d.market !== market.value || d.pair !== symbol.value || d.interval !== interval.value) return
        handlers.onKline({
          time: d.time as number,
          open: d.open as number,
          high: d.high as number,
          low: d.low as number,
          close: d.close as number,
          volume: d.volume as number,
        })
      } else if (msg.type === 'analysis') {
        const d = msg.data
        if (d.market !== market.value || d.symbol !== symbol.value || d.interval !== interval.value) return
        handlers.onAnalysis(d as unknown as AnalysisResult)
      }
    }

    socket.onclose = () => {
      if (gen !== generation) return
      status.value = 'closed'
      scheduleReconnect()
    }
    socket.onerror = () => {
      // onclose 会随后触发，由它安排重连
    }
  }

  function scheduleReconnect() {
    if (reconnectTimer) clearTimeout(reconnectTimer)
    reconnectTimer = setTimeout(() => {
      reconnectDelay = Math.min(reconnectDelay * 2, 15000)
      connect()
    }, reconnectDelay)
  }

  watch([market, symbol, interval, segments], () => {
    reconnectDelay = 1000
    connect()
  })

  connect()

  onBeforeUnmount(disconnect)

  return { status }
}
