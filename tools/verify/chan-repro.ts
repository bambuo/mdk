// 修复后复现性验证：内部窗口固定为 700 根 → 不同显示窗口在重叠区的信号必须完全一致
const BASE = 'http://localhost:5099'
async function getJson(u) { const r = await fetch(u); if (!r.ok) throw new Error(u + ' ' + r.status); return r.json() }
const fmt = (t) => new Date(t * 1000).toISOString().slice(5, 16)

let bad = 0
for (const [symbol, interval] of [['BTCUSDT', '1h'], ['ETHUSDT', '15m'], ['SOLUSDT', '4h'], ['BNBUSDT', '1h']]) {
  const [a, b, c] = await Promise.all([300, 500, 1000].map(l => getJson(`${BASE}/api/analysis?market=spot&symbol=${symbol}&interval=${interval}&limit=${l}`)))
  const sigs = (x) => x.signals.filter(s => s.source === '缠论').map(s => `${s.side}|${s.time}|${s.stopPrice.toFixed(4)}|${s.note}`)
  const sa = sigs(a), sb = sigs(b), sc = sigs(c)

  // 重叠区 = 最短显示窗口覆盖的范围（用 300 根窗口的起点为界）
  const start300 = a.series.chanStroke.length ? Date.now() : 0
  const kl = await getJson(`${BASE}/api/klines?market=spot&symbol=${symbol}&interval=${interval}&limit=300`)
  const overlapStart = kl.candles[0].time
  const inOverlap = (list, times) => list.filter((_, i) => true) // 占位
  const filter = (x) => x.signals.filter(s => s.source === '缠论' && s.time >= overlapStart).map(s => `${s.side}|${s.time}|${s.stopPrice.toFixed(4)}|${s.note}`).sort()

  const fa = filter(a), fb = filter(b), fc = filter(c)
  const same = JSON.stringify(fa) === JSON.stringify(fb) && JSON.stringify(fb) === JSON.stringify(fc)
  if (!same) bad++
  console.log(`${same ? '✓' : '✗'} ${symbol} ${interval}：重叠区(${fmt(overlapStart)} 之后) 信号集合 — 显示窗口 300/500/1000 分别 ${fa.length}/${fb.length}/${fc.length} 个`)
  if (!same) {
    const onlyIn = (x, y, nx, ny) => x.filter(v => !y.includes(v)).forEach(v => console.log(`    仅${nx}有: ${v}`))
    onlyIn(fa, fb, 300, 500); onlyIn(fb, fa, 500, 300)
    onlyIn(fb, fc, 500, 1000); onlyIn(fc, fb, 1000, 500)
  }
  // 中枢一致性：chanZg 在重叠区的取值
  const zgOf = (x, n) => { const z = x.series.chanZg; const off = z.length - n; return z.slice(off).map(v => v == null ? -1 : Math.round(v * 100)) }
  const zA = zgOf(a, 300), zB = zgOf(b, 300), zC = zgOf(c, 300)
  const zgSame = JSON.stringify(zA) === JSON.stringify(zB) && JSON.stringify(zB) === JSON.stringify(zC)
  if (!zgSame) bad++
  console.log(`  ${zgSame ? '✓' : '✗'} 最近 300 根的中枢带取值在三者间一致`)
}
console.log(bad === 0 ? '\n复现性验证通过：内部窗口钉死后，不同显示窗口结果一致' : `\n${bad} 项不一致`)
