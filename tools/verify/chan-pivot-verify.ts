// 中枢正确性独立复算：用接口返回的 chanStroke（笔端点）重建笔，再验证每个中枢的
// Zg = 前三笔高点的最小值、Zd = 前三笔低点的最大值，以及区间是否与笔区间一致
const BASE = 'http://localhost:5099'
const symbol = process.argv[2] ?? 'WLDUSDT'
const interval = process.argv[3] ?? '1h'

const [kl, an] = await Promise.all([
  fetch(`${BASE}/api/klines?market=spot&symbol=${symbol}&interval=${interval}&limit=500`).then(r => r.json()),
  fetch(`${BASE}/api/analysis?market=spot&symbol=${symbol}&interval=${interval}&limit=500`).then(r => r.json()),
])
const candles = kl.candles
const idxOf = new Map(candles.map((c, i) => [c.time, i]))
const stroke = an.series.chanStroke
const chan = an.chan

// ① 从 chanStroke 序列重建笔端点（时间 + 价格，按时间顺序）
const pts = []
for (let i = 0; i < stroke.length; i++) if (stroke[i] != null) pts.push({ time: candles[i].time, price: stroke[i], bar: i })

// ② 重建笔（相邻端点成笔）
const strokes = []
for (let i = 1; i < pts.length; i++) {
  strokes.push({
    from: pts[i - 1].time, to: pts[i].time,
    fromBar: pts[i - 1].bar, toBar: pts[i].bar,
    high: Math.max(pts[i - 1].price, pts[i].price),
    low: Math.min(pts[i - 1].price, pts[i].price),
    isUp: pts[i].price > pts[i - 1].price,
  })
}

console.log(`【${symbol} ${interval}】K线 ${candles.length} 根，笔端点 ${pts.length} 个 → 重建笔 ${strokes.length} 条`)
console.log(`摘要：中枢数=${chan.pivotCount} 返回区间数=${chan.pivots.length} 最新中枢=[${chan.pivotZd}, ${chan.pivotZg}] `)
console.log(`     最后一根收盘=${candles[candles.length - 1].close}  摘要 priceInPivot=${chan.priceInPivot}`)

let bad = 0
console.log('\n逐中枢核对（Zg/Zd 是否为"成形三笔"的重叠区间）：')
for (const p of chan.pivots) {
  const startBar = idxOf.get(p.fromTime), endBar = idxOf.get(p.toTime)
  // 该中枢区间内的笔（按区间覆盖判定）
  const inner = strokes.filter(s => s.fromBar >= startBar - 1 && s.toBar <= endBar + 1)
  const first3 = inner.slice(0, 3)
  if (first3.length < 3) { console.log(`  ? ${p.fromTime}~${p.toTime} 区间内笔不足 3（${first3.length}）`); bad++; continue }
  const zg = Math.min(...first3.map(s => s.high))
  const zd = Math.max(...first3.map(s => s.low))
  const zgOk = Math.abs(zg - p.zg) < 1e-6, zdOk = Math.abs(zd - p.zd) < 1e-6
  const spanOk = Math.abs(inner[0].fromBar - startBar) <= 1 && Math.abs(inner[inner.length - 1].toBar - endBar) <= 1
  const date = (t) => new Date(t * 1000).toISOString().slice(5, 16)
  const tag = zgOk && zdOk && spanOk ? '✓' : '✗'
  if (tag === '✗') bad++
  console.log(`  ${tag} ${date(p.fromTime)}~${date(p.toTime)} ${p.strokes}笔  区间[${p.zd}, ${p.zg}]  复算[${zd.toFixed(4)}, ${zg.toFixed(4)}]` +
    `${zgOk ? '' : ' ←Zg不符'}${zdOk ? '' : ' ←Zd不符'}${spanOk ? '' : ' ←横向范围不符'}`)
}

// ③ 中枢互不重叠 + Zg>Zd
let overlap = 0
const sorted = [...chan.pivots].sort((a, b) => a.fromTime - b.fromTime)
for (let i = 1; i < sorted.length; i++) if (sorted[i].fromTime < sorted[i - 1].toTime) overlap++
const orderBad = chan.pivots.filter(p => !(p.zg > p.zd)).length
console.log(`\n区间互不重叠: ${overlap === 0 ? '✓' : '✗ ' + overlap + ' 处重叠'}   Zg>Zd: ${orderBad === 0 ? '✓' : '✗'}`)

// ④ 摘要一致性：priceInPivot 是否等于"现价落在最新中枢区间内"
const latest = chan.pivots[chan.pivots.length - 1]
const lastClose = candles[candles.length - 1].close
const expectIn = lastClose >= latest.zd && lastClose <= latest.zg
console.log(`\n摘要一致性：最新中枢=[${latest.zd}, ${latest.zg}]，最后收盘=${lastClose}`)
console.log(`  期望 priceInPivot=${expectIn}，接口返回=${chan.priceInPivot} → ${expectIn === chan.priceInPivot ? '✓ 一致' : '✗ 不一致（疑似缺陷）'}`)
console.log(`\n结论：${bad === 0 && overlap === 0 && orderBad === 0 ? '中枢几何全部通过独立复算' : '存在 ' + bad + ' 处不一致'}`)
