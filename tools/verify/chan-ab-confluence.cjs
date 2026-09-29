// 级别共振 A/B：按日志里的 confluence 标签分组，用**事先声明的判据**给出结论。
//
// 事先声明的判据（在跑数据之前写定，避免事后挑选）：
//   共振组需同时满足：① 独立波次 ≥50 ② 波次口径超额 t ≥2.5（比 2 更严，因多重比较）
//   ③ 中位超额 >0 ④ 单一标的占比 ≤40%
//   否则结论为"证据不足，不启用"。
const rows = require('./signals-db.cjs').load(process.argv[2])
  .filter(r => r.outcome && r.outcome.status === 'ok' && r.confluence)

const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN
const std = a => { const m = mean(a); return Math.sqrt(mean(a.map(v => (v - m) ** 2))) }
const tstat = a => (a.length > 1 && std(a) > 0) ? mean(a) / (std(a) / Math.sqrt(a.length)) : NaN
const median = a => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.floor(s.length / 2)] : NaN }

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
  if (!items.length) { console.log(`${label.padEnd(16)} n=0`); return null }
  const ev = episodes(items)
  const ex = items.map(r => r.outcome.excess)
  const exEp = ev.map(r => r.outcome.excess)
  const win = items.filter(r => r.outcome.ret > 0).length / items.length
  const net = items.filter(r => r.outcome.netPositive).length / items.length
  const top = Math.max(...Object.values(items.reduce((a, r) => (a[r.symbol] = (a[r.symbol] ?? 0) + 1, a), {}))) / items.length
  console.log(`${label.padEnd(16)} n=${String(items.length).padStart(4)} 波次=${String(ev.length).padStart(3)} 胜率=${(100 * win).toFixed(1)}% 超额=${(100 * mean(ex)).toFixed(2)}% 波次超额=${(100 * mean(exEp)).toFixed(2)}%(t=${tstat(exEp).toFixed(2)}) 中位超额=${(100 * median(ex)).toFixed(2)}% 扣费后为正=${(100 * net).toFixed(0)}% 单标的=${(100 * top).toFixed(0)}%`)
  return { n: items.length, episodes: ev.length, win, meanEx: mean(ex), tEp: tstat(exEp), medEx: median(ex), net, top }
}

console.log(`含 confluence 标签的已评估样本：${rows.length} 条（标的 ${new Set(rows.map(r => r.symbol)).size} 个，周期 ${[...new Set(rows.map(r => r.interval))].join('/')}）\n`)
const aligned = report('级别共振', rows.filter(r => r.confluence === 'aligned'))
report('逆向共振', rows.filter(r => r.confluence === 'counter'))
const none = report('无共振', rows.filter(r => r.confluence === 'none'))

console.log('\n=== 事先声明判据 ===')
if (!aligned) {
  console.log('共振组无样本 → 证据不足，不启用')
} else {
  const checks = [
    ['① 独立波次 ≥50', aligned.episodes >= 50, `实际 ${aligned.episodes}`],
    ['② 波次口径 t ≥2.5', aligned.tEp >= 2.5, `实际 t=${aligned.tEp.toFixed(2)}`],
    ['③ 中位超额 >0', aligned.medEx > 0, `实际 ${(100 * aligned.medEx).toFixed(2)}%`],
    ['④ 单一标的占比 ≤40%', aligned.top <= 0.4, `实际 ${(100 * aligned.top).toFixed(0)}%`],
  ]
  for (const [name, ok, detail] of checks) console.log(`  ${ok ? '✓' : '✗'} ${name}（${detail}）`)
  const pass = checks.every(c => c[1])
  console.log(`\n结论：${pass ? '判据全部满足 → 可启用为"信号质量标签"（并可考虑做并发多级别引擎）' : '未全部满足 → 证据不足，仅作展示标签，不据此过滤/不升级提醒'}`)
  if (none) {
    const lift = aligned.meanEx - none.meanEx
    console.log(`参考：共振组与非共振组的超额差 ${(100 * lift).toFixed(2)}pp（仅描述，不作为判据）`)
  }
}
