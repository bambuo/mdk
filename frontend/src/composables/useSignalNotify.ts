import { ref } from 'vue'

/**
 * 信号桌面提醒：图表路径（WS 推送）与监控列表路径（轮询）共用同一套去重与冷却。
 *
 * 口径（与界面「提醒」按钮一致）：
 * - 只提醒**已确认**的缠论买卖点（盘中预警不提醒）；
 * - 信号时间须在 **6 小时**内（防历史/回填样本打扰）；
 * - 同一信号只提醒一次（键：标的|周期|方向|时间）；
 * - **同向冷却**：同一 标的+周期+方向 在 COOLDOWN_MINUTES 内只提醒一次
 *   （否则 5m 周期上相邻K线会连续弹同向信号）；
 *   **反向信号不冷却**——多空翻转比同向重复更值得知道。
 * - 首次拿到数据时把已有信号标记为"已见"（不打扰刚打开的页面）。
 */

/** 同标的冷却时长（分钟）——口径常量，不是配置项（见 config 守卫纪律：配置只写真实覆盖） */
export const COOLDOWN_MINUTES = 30

/** 只提醒 6 小时内的信号 */
const RECENT_SECONDS = 6 * 3600

export interface NotifiableSignal {
  symbol: string
  baseAsset: string
  quoteAsset: string
  interval: string
  side: 'buy' | 'sell'
  time: number
  note: string | null
  price: number
  stopPrice: number | null
}

/** 通知载荷（便于测试与复用） */
export interface NotificationPayload {
  key: string
  cooldownKey: string
  title: string
  body: string
  tag: string
}

const notifyEnabled = ref(localStorage.getItem('mdk.notify') === '1')
/** 已见信号（内存态：刷新后由"首次标记"重新填充，不会重复弹） */
const seen = new Set<string>()
/** 各冷却键最近一次提醒时刻（毫秒） */
const lastNotified = new Map<string, number>()

export function useSignalNotify() {
  function setEnabled(next: boolean): void {
    notifyEnabled.value = next
    localStorage.setItem('mdk.notify', next ? '1' : '0')
  }

  /** 把当前已有信号标记为已见（页面打开/首次轮询时调用，避免喷一堆通知） */
  function primeSeen(signals: NotifiableSignal[]): void {
    for (const s of signals) if (s.side === 'buy' || s.side === 'sell') seen.add(keyOf(s))
  }

  /** 由信号生成通知载荷（纯函数：去重、时效、冷却判定都在这里，便于复核） */
  function buildPayloads(signals: NotifiableSignal[], nowMs = Date.now()): NotificationPayload[] {
    const out: NotificationPayload[] = []
    for (const s of signals) {
      const key = keyOf(s)
      if (seen.has(key)) continue
      seen.add(key)                                   // 无论是否提醒都记为已见（只判一次）
      if (!notifyEnabled.value) continue
      if (s.time < nowMs / 1000 - RECENT_SECONDS) continue
      const cooldownKey = `${s.symbol}|${s.interval}|${s.side}`
      const last = lastNotified.get(cooldownKey)
      if (last != null && nowMs - last < COOLDOWN_MINUTES * 60_000) continue
      lastNotified.set(cooldownKey, nowMs)
      out.push({
        key,
        cooldownKey,
        title: `${s.baseAsset}/${s.quoteAsset} ${s.interval} ${s.side === 'buy' ? '买点' : '卖点'}`,
        body: `${s.note ?? ''}\n价格 ${s.price}${s.stopPrice != null ? ` · 止损参考 ${s.stopPrice}` : ''}`,
        tag: `${s.symbol}-${s.interval}-${s.time}-${s.side}`,
      })
    }
    return out
  }

  /** 弹通知（浏览器不支持或无权限时静默跳过）。
   *  用 globalThis 而非 window：浏览器里两者等价，但模块因此可在非浏览器环境被测试驱动。 */
  function emit(payloads: NotificationPayload[]): void {
    const ctor = (globalThis as { Notification?: typeof Notification }).Notification
    if (!ctor || ctor.permission !== 'granted') return
    for (const p of payloads) new ctor(p.title, { body: p.body, tag: p.tag })
  }

  /** 一步到位：给一批信号，去重/冷却后弹通知 */
  function notify(signals: NotifiableSignal[], nowMs = Date.now()): NotificationPayload[] {
    const payloads = buildPayloads(signals, nowMs)
    emit(payloads)
    return payloads
  }

  function keyOf(s: NotifiableSignal): string {
    return `${s.symbol}|${s.interval}|${s.side}|${s.time}|${(s.note ?? '').slice(0, 8)}`
  }

  return { notifyEnabled, setEnabled, primeSeen, notify, buildPayloads, keyOf, seen, lastNotified }
}
