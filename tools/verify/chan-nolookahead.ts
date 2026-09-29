// 精确版无未来函数验证：只在两个窗口的重叠区间内比较，并打印边界与差异明细
const BASE = 'http://localhost:5099'
async function getJson(u) { const r = await fetch(u); if (!r.ok) throw new Error(u + ' ' + r.status); return r.json() }
const fmt = (t) => new Date(t * 1000).toISOString().slice(5, 16)

for (const [symbol, interval, full, cut] of [['BTCUSDT', '1h', 500, 460], ['ETHUSDT', '15m', 500, 460], ['BNBUSDT', '1h', 500, 450]]) {
  const [fullAn, cutAn, cutKl, fullKl] = await Promise.all([
    getJson(`${BASE}/api/analysis?market=spot&symbol=${symbol}&interval=${interval}&limit=${full}`),
    getJson(`${BASE}/api/analysis?market=spot&symbol=${symbol}&interval=${interval}&limit=${cut}`),
    getJson(`${BASE}/api/klines?market=spot&symbol=${symbol}&interval=${interval}&limit=${cut}`),
    getJson(`${BASE}/api/klines?market=spot&symbol=${symbol}&interval=${interval}&limit=${full}`),
  ])
  const cutStart = cutKl.candles[0].time
  const cutEnd = cutKl.candles[cutKl.candles.length - 1].time
  const fullStart = fullKl.candles[0].time
  const chanOf = (an) => an.signals.filter(s => s.source === '缠论' && s.time >= cutStart && s.time <= cutEnd)
  const fullSig = chanOf(fullAn).map(s => `${s.side}|${s.time}|${s.note.slice(0, 14)}`)
  const cutSig = chanOf(cutAn).map(s => `${s.side}|${s.time}|${s.note.slice(0, 14)}`)
  const missing = fullSig.filter(x => !cutSig.includes(x))
  const extra = cutSig.filter(x => !fullSig.includes(x))

  console.log(`\n[${symbol} ${interval}] 完整窗口 ${fmt(fullStart)}~${fmt(cutEnd)} | 截断窗口 ${fmt(cutStart)}~${fmt(cutEnd)}`)
  console.log(`  重叠区间内：完整 ${fullSig.length} 个 / 截断 ${cutSig.length} 个`)
  if (missing.length) {
    console.log(`  ✗ 完整有而截断没有（疑似依赖未来数据）：`)
    for (const m of missing) console.log(`      ${m}`)
  } else console.log('  ✓ 完整窗口的重叠区信号在截断窗口中全部存在')
  if (extra.length) {
    console.log(`  ⚠ 截断有而完整没有（结构差异导致）：`)
    for (const e of extra) console.log(`      ${e}`)
  }
}
