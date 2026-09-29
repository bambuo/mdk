// 多周期共振的既有代理指标检验（用 90 天回填样本，无需新代码）：
// ① 高周期均线对齐（trendAligned，信号时 4h 收盘 vs 4h EMA50）
// ② 多周期信号共振：本级别信号之前 K 根高周期K线内是否出现同向高周期缠论信号
// ③ 市场状态（ADX）
// ④ 组合（共振 + 顺高周期）
const fs = require('fs')
const rows = fs.readFileSync('/Users/johana/Desktop/mdk/backend/Mdk.Api/data/signals.jsonl', 'utf8')
  .trim().split('\n').map(l => JSON.parse(l)).filter(r => r.outcome && r.outcome.status === 'ok')

const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN
const std = a => { const m = mean(a); return Math.sqrt(mean(a.map(v => (v - m) ** 2))) }
const t = a => (a.length > 1 && std(a) > 0) ? mean(a) / (std(a) / Math.sqrt(a.length)) : NaN

function episodes(items) {
  const out = []
  for (const key of new Set(items.map(r => `${r.symbol}|${r.interval}|${r.side}`))) {
    const g = items.filter(r => `${r.symbol}|${r.interval}|${r.side}` === key).sort((a, b) => a.time - b.time)
    const barSec = { '15m': 900, '1h': 3600, '4h': 14400 }[g[0].interval] ?? 3600
    let prev = null
    for (const r of g) { if (prev === null || r.time - prev > barSec * 24) out.push(r); prev = r.time }
  }
  return out
}

function report(label, items) {
  if (!items.length) return console.log(`${label.padEnd(30)} n=0`)
  const ev = episodes(items)
  const ex = items.map(r => r.outcome.excess)
  const exEp = ev.map(r => r.outcome.excess)
  const win = items.filter(r => r.outcome.ret > 0).length / items.length
  const net = items.filter(r => r.outcome.netPositive).length / items.length
  console.log(`${label.padEnd(30)} n=${String(items.length).padStart(4)} 波次=${String(ev.length).padStart(3)} 胜率=${(100 * win).toFixed(1)}%  超额=${(100 * mean(ex)).toFixed(2)}%(t=${t(ex).toFixed(2)})  波次超额=${(100 * mean(exEp)).toFixed(2)}%(t=${t(exEp).toFixed(2)})  扣费后为正=${(100 * net).toFixed(0)}%`)
}

// 只看缠论
const chan = rows.filter(r => r.source === '缠论')
const chan1h = chan.filter(r => r.interval === '1h')
const chan4h = chan.filter(r => r.interval === '4h')

console.log(`样本：缠论 ${chan.length} 条（1h ${chan1h.length} / 4h ${chan4h.length}）`)

console.log('\n=== ① 高周期均线对齐（信号时 4h 收盘 vs 4h EMA50）===')
report('全部缠论 1h', chan1h)
report('  顺高周期(4h 多头)', chan1h.filter(r => r.trendAligned === true))
report('  逆高周期', chan1h.filter(r => r.trendAligned === false))

// ② 多周期信号共振：以 4h 缠论信号为"高周期确认"，1h 信号前 2 根 4h（8 小时）内含同向 4h 信号
const H4 = 4 * 3600
const bySymbol = {}
for (const r of chan4h) (bySymbol[r.symbol] ??= []).push(r)

function hasHtfAgreement(sig, windowBars) {
  const list = bySymbol[sig.symbol]
  if (!list) return false
  const from = sig.time - windowBars * H4
  return list.some(h => h.side === sig.side && h.time >= from && h.time <= sig.time)
}

console.log('\n=== ② 多周期信号共振（1h 信号前 N×4h 内出现同向 4h 缠论信号）===')
for (const w of [1, 2, 3, 6]) {
  const agree = chan1h.filter(s => hasHtfAgreement(s, w))
  report(`  共振窗口 ${w}×4h`, agree)
}
report('  无共振（前 2×4h）', chan1h.filter(s => !hasHtfAgreement(s, 2)))

console.log('\n=== ③ 市场状态（信号时 ADX）===')
report('  ADX ≥ 30', chan1h.filter(r => r.adx != null && r.adx >= 30))
report('  20 ≤ ADX < 30', chan1h.filter(r => r.adx != null && r.adx >= 20 && r.adx < 30))
report('  ADX < 20', chan1h.filter(r => r.adx != null && r.adx < 20))

console.log('\n=== ④ 组合：多周期共振 + 顺高周期 + 趋势状态 ===')
const combo = chan1h.filter(s => hasHtfAgreement(s, 2) && s.trendAligned === true && s.adx != null && s.adx >= 25)
report('  共振 + 顺高周期 + ADX≥25', combo)

console.log('\n说明：|t|<2 视为与零不可区分；n<30 的组不作结论。')
