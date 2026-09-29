/** 展示格式化工具（时间统一按 UTC+8 显示） */

export function pricePrecision(price: number): number {
  const p = Math.abs(price)
  if (p >= 100) return 2
  if (p >= 1) return 4
  if (p >= 0.01) return 5
  return 7
}

export function formatPrice(price: number | null | undefined): string {
  if (price == null || !isFinite(price)) return '—'
  const digits = pricePrecision(price)
  return price.toLocaleString('en-US', {
    minimumFractionDigits: Math.min(2, digits),
    maximumFractionDigits: digits,
  })
}

export function formatVolume(v: number): string {
  if (v >= 1e9) return `${(v / 1e9).toFixed(2)}B`
  if (v >= 1e6) return `${(v / 1e6).toFixed(2)}M`
  if (v >= 1e3) return `${(v / 1e3).toFixed(1)}K`
  return v.toFixed(0)
}

export function formatPct(v: number): string {
  return `${v >= 0 ? '+' : ''}${v.toFixed(2)}%`
}

const UTC8_OFFSET = 8 * 3600

function utc8Parts(sec: number): Date {
  return new Date((sec + UTC8_OFFSET) * 1000)
}

/** K线时间 → "MM-DD HH:mm"（UTC+8） */
export function formatTime(sec: number): string {
  const d = utc8Parts(sec)
  const mm = String(d.getUTCMonth() + 1).padStart(2, '0')
  const dd = String(d.getUTCDate()).padStart(2, '0')
  const hh = String(d.getUTCHours()).padStart(2, '0')
  const mi = String(d.getUTCMinutes()).padStart(2, '0')
  return `${mm}-${dd} ${hh}:${mi}`
}

/** 信号/点位时间 → "YYYY-MM-DD HH:mm"（UTC+8） */
export function formatTimeFull(sec: number): string {
  const d = utc8Parts(sec)
  const yyyy = d.getUTCFullYear()
  return `${yyyy}-${formatTime(sec)}`
}

/** 周期 → 秒数（用于判断实时K线数据是否出现断层） */
export function intervalSeconds(interval: string): number {
  const match = /^(\d+)([mhdw])$/.exec(interval)
  if (!match) return 60
  const value = Number(match[1])
  const unit = match[2]
  const unitSeconds = unit === 'm' ? 60 : unit === 'h' ? 3600 : unit === 'd' ? 86400 : 604800
  return value * unitSeconds
}

