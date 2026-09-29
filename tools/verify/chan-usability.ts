// 缠论信号可用性测量（新规则）：频率 / 结构滞后 / 入场滞后 / 止损距离
const BASE = 'http://localhost:5099'
async function getJson(u) { const r = await fetch(u); if (!r.ok) throw new Error(u + ' ' + r.status); return r.json() }

const barsPerDay = { '15m': 96, '1h': 24, '4h': 6 }
const rows = []

for (const [symbol, interval] of [['BTCUSDT', '1h'], ['ETHUSDT', '1h'], ['SOLUSDT', '1h'], ['BNBUSDT', '1h'], ['ETHUSDT', '15m'], ['SOLUSDT', '4h'], ['XRPUSDT', '1h'], ['DOGEUSDT', '1h'], ['ADAUSDT', '15m'], ['AVAXUSDT', '1h']]) {
  const [kl, an] = await Promise.all([
    getJson(`${BASE}/api/klines?market=spot&symbol=${symbol}&interval=${interval}&limit=500`),
    getJson(`${BASE}/api/analysis?market=spot&symbol=${symbol}&interval=${interval}&limit=500`),
  ])
  const candles = kl.candles
  const idxOf = (t) => candles.findIndex(c => c.time === t)
  const chan = an.signals.filter(s => s.source === '缠论')
  if (!chan.length) { console.log(`${symbol} ${interval}: 无信号`); continue }

  for (const s of chan) {
    const m = s.note.match(/参考(?:低|高)点\s*([\d.]+)|回抽(?:低|高)点\s*([\d.]+)/)
    const ref = m ? parseFloat(m[1] ?? m[2]) : null
    const i = idxOf(s.time)
    // 结构参考点所在K线（在该K线前后 60 根内找与参考价最接近的极值）
    let refBar = null, best = Infinity
    for (let k = Math.max(0, i - 60); k <= i; k++) {
      const extreme = s.side === 'buy' ? candles[k].low : candles[k].high
      const d = ref == null ? Infinity : Math.abs(extreme - ref)
      if (d < best) { best = d; refBar = k }
    }
    const lagBars = refBar == null ? null : i - refBar
    const missedPct = ref == null ? null : (s.side === 'buy' ? (s.price - ref) / ref : (ref - s.price) / ref) * 100
    const stopPct = Math.abs(s.price - s.stopPrice) / s.price * 100
    rows.push({ symbol, interval, kind: s.note.slice(1, 3), side: s.side, lagBars, missedPct, stopPct, adx: s.adx })
  }
  const perMonth = chan.length / (500 / barsPerDay[interval] / 30)
  console.log(`${symbol.padEnd(9)} ${interval.padEnd(4)} 信号 ${chan.length} 个（≈${perMonth.toFixed(1)}/月）`)
}

const nums = (arr) => arr.filter(v => v != null)
const stat = (name, arr, unit = '') => {
  const a = nums(arr).sort((x, y) => x - y)
  if (!a.length) return console.log(`  ${name}: 无数据`)
  const med = a[Math.floor(a.length / 2)]
  console.log(`  ${name}: 中位 ${med.toFixed(1)}${unit}（最小 ${a[0].toFixed(1)} / 最大 ${a[a.length - 1].toFixed(1)}，n=${a.length}）`)
}

console.log(`\n=== 汇总（${rows.length} 条缠论信号）===`)
stat('结构滞后（参考点 → 记账K线）', rows.map(r => r.lagBars), ' 根')
stat('入场滞后（已错过的幅度）', rows.map(r => r.missedPct), '%')
stat('止损距离（入场 → 结构失效位）', rows.map(r => r.stopPct), '%')
stat('信号时 ADX', rows.map(r => r.adx))
const kinds = {}
for (const r of rows) kinds[r.kind] = (kinds[r.kind] ?? 0) + 1
console.log('  类别分布:', JSON.stringify(kinds))
console.log('  买卖分布:', JSON.stringify(rows.reduce((a, r) => (a[r.side] = (a[r.side] ?? 0) + 1, a), {})))
