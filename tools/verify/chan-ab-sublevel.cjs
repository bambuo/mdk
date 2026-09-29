// A/B 对照：次级别确认开关对缠论回填样本表现的影响
// 读 backend/Mdk.Api/data/signals.jsonl，比较 origin=backfill（含次级别确认）与 origin=backfill-nosub（关闭）
const fs = require('fs')
const path = process.argv[2] ?? '/Users/johana/Desktop/mdk/backend/Mdk.Api/data/signals.jsonl'
const rows = fs.readFileSync(path, 'utf8').trim().split('\n').map(l => JSON.parse(l))

const mean = a => a.reduce((x, y) => x + y, 0) / a.length
const std = a => { const m = mean(a); return Math.sqrt(mean(a.map(v => (v - m) ** 2))) }
const tstat = a => (a.length > 1 && std(a) > 0) ? mean(a) / (std(a) / Math.sqrt(a.length)) : NaN
const median = a => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.floor(s.length / 2)] : NaN }

// 波次代表样本：同币种/周期/方向、间隔 ≤24 根归为一波
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

function report(label, origin) {
  const all = rows.filter(r => r.origin === origin && r.outcome && r.outcome.status === 'ok')
  const ev = episodes(all)
  if (!all.length) { console.log(`${label}: 无已评估样本`); return null }
  const ex = all.map(r => r.outcome.excess)
  const exEp = ev.map(r => r.outcome.excess)
  const stop = all.filter(r => r.outcome.stopHit).length / all.length
  const net = all.filter(r => r.outcome.netPositive).length / all.length
  const win = all.filter(r => r.outcome.ret > 0).length / all.length
  console.log(`${label.padEnd(22)} n=${String(all.length).padStart(4)} 波次=${String(ev.length).padStart(3)}  胜率=${(100 * win).toFixed(1)}%  超额=${(100 * mean(ex)).toFixed(2)}%(t=${tstat(ex).toFixed(2)})  波次超额=${(100 * mean(exEp)).toFixed(2)}%(t=${tstat(exEp).toFixed(2)})  中位超额=${(100 * median(ex)).toFixed(2)}%  扣费后为正=${(100 * net).toFixed(0)}%  止损=${(100 * stop).toFixed(0)}%`)
  return { n: all.length, episodes: ev.length, win, meanEx: mean(ex), t: tstat(ex), net, stop }
}

console.log('=== 缠论回填 A/B：次级别确认开关 ===')
const on = report('含次级别确认', 'backfill')
const off = report('关闭次级别确认', 'backfill-nosub')

if (on && off) {
  console.log(`\n差异（含确认 − 关闭）：胜率 ${((on.win - off.win) * 100).toFixed(1)}pp，超额 ${((on.meanEx - off.meanEx) * 100).toFixed(2)}pp，扣费后为正 ${((on.net - off.net) * 100).toFixed(0)}pp，止损命中 ${((on.stop - off.stop) * 100).toFixed(0)}pp`)
  console.log('说明：差异需在样本足够（波次 ≥30）时才有统计意义；|t|<2 视为不可区分。')
}
