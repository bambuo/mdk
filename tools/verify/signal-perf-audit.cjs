// 缠论绩效的严格检验：显著性 / 波次口径 / 分类别与分标的拆解 / 与同期基线对比
// 数据来源：SQLite 台账（见 signals-db.cjs）
const rows = require('./signals-db.cjs').load()
  .filter(r => r.source === '缠论' && r.outcome && r.outcome.status === 'ok')

const mean = a => a.reduce((x, y) => x + y, 0) / a.length
const std = a => { const m = mean(a); return Math.sqrt(mean(a.map(v => (v - m) ** 2))) }
const tstat = a => mean(a) / (std(a) / Math.sqrt(a.length))

function report(title, items) {
  if (!items.length) return console.log(`${title.padEnd(26)} n=0`)
  const ex = items.map(r => r.outcome.excess)
  const wins = items.filter(r => r.outcome.ret > 0).length
  const net = items.filter(r => r.outcome.netPositive).length
  console.log(`${title.padEnd(26)} n=${String(items.length).padStart(3)} 胜率=${(100 * wins / items.length).toFixed(0)}%  平均超额=${(100 * mean(ex)).toFixed(2)}%  t=${tstat(ex).toFixed(2)}  扣费后为正=${(100 * net / items.length).toFixed(0)}%`)
}

// 波次去重（同币种/周期/方向、间隔 ≤24 根归为一波，取波内首条）
function episodes(items) {
  const out = []
  for (const key of new Set(items.map(r => `${r.symbol}|${r.interval}|${r.side}`))) {
    const group = items.filter(r => `${r.symbol}|${r.interval}|${r.side}` === key).sort((a, b) => a.time - b.time)
    const barSec = { '15m': 900, '1h': 3600, '4h': 14400 }[group[0].interval] ?? 3600
    let prev = null
    for (const r of group) { if (prev === null || r.time - prev > barSec * 24) out.push(r); prev = r.time }
  }
  return out
}

console.log(`已评估缠论样本 ${rows.length} 条，覆盖 ${new Set(rows.map(r => r.symbol)).size} 个标的`)
const span = (Math.max(...rows.map(r => r.recordedAt)) - Math.min(...rows.map(r => r.recordedAt))) / 86400
console.log(`记录时间跨度 ${span.toFixed(2)} 天（信号本身覆盖更长的历史K线区间）`)
console.log(`时间范围：${new Date(Math.min(...rows.map(r => r.time)) * 1000).toISOString().slice(0, 16)} ~ ${new Date(Math.max(...rows.map(r => r.time)) * 1000).toISOString().slice(0, 16)}`)

console.log('\n=== 全部 vs 波次去重（独立性修正）===')
report('全部样本', rows)
report('波次去重后（每波首条）', episodes(rows))

console.log('\n=== 分类别 ===')
for (const k of ['1买', '2买', '3买', '1卖', '2卖', '3卖']) report(`  ${k}`, rows.filter(r => r.note.slice(1, 3) === k))

console.log('\n=== 分周期 ===')
for (const itv of ['15m', '1h', '4h']) report(`  ${itv}`, rows.filter(r => r.interval === itv))

console.log('\n=== 分标的（1h）===')
const syms = [...new Set(rows.filter(r => r.interval === '1h').map(r => r.symbol))]
for (const s of syms.slice(0, 8)) report(`  ${s}`, rows.filter(r => r.symbol === s && r.interval === '1h'))

console.log('\n=== 方向拆分 ===')
report('买点', rows.filter(r => r.side === 'buy'))
report('卖点', rows.filter(r => r.side === 'sell'))

console.log('\n=== 样本充足度 ===')
const ep = episodes(rows)
console.log(`  原始 n=${rows.length}，波次 n=${ep.length}；按"可参考"门槛（n≥30）看：原始达标、波次${ep.length >= 30 ? '达' : '未达'}标`)
console.log(`  按每波的超额计算 t=${tstat(ep.map(r => r.outcome.excess)).toFixed(2)}（|t|<2 时统计上不可区分于零）`)
