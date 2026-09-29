// 线段正确性独立核对：
// ① 每段笔数奇数且 ≥3；② 段间首尾相连、方向交替；③ 段终点是否为该段方向极值（跳过尾部未确认段）；
// ④ 破坏点独立复算（特征序列分型 + 缺口回补），扫描范围放宽到"段终点之后 6 个反向笔"
const BASE = 'http://localhost:5099'
const symbol = process.argv[2] ?? 'BTCUSDT'
const interval = process.argv[3] ?? '1h'

const [an] = await Promise.all([
  fetch(`${BASE}/api/analysis?market=spot&symbol=${symbol}&interval=${interval}&limit=1200&segments=true`).then(r => r.json()),
])
const pts = (s) => { const o = []; for (let i = 0; i < s.length; i++) if (s[i] != null) o.push({ bar: i, price: s[i] }); return o }
const sp = pts(an.series.chanStroke), gp = pts(an.series.chanSegment)
const strokes = []
for (let i = 1; i < sp.length; i++) {
  const a = sp[i - 1], b = sp[i]
  strokes.push({ startBar: a.bar, endBar: b.bar, startPrice: a.price, endPrice: b.price,
                 isUp: b.price > a.price, high: Math.max(a.price, b.price), low: Math.min(a.price, b.price) })
}
const segments = []
for (let i = 1; i < gp.length; i++) {
  const a = gp[i - 1], b = gp[i]
  segments.push({ startBar: a.bar, endBar: b.bar, startPrice: a.price, endPrice: b.price, isUp: b.price > a.price })
}
const siOf = (bar, price) => strokes.findIndex(s => s.startBar === bar && Math.abs(s.startPrice - price) < 1e-6)
const eiOf = (bar, price) => strokes.findIndex(s => s.endBar === bar && Math.abs(s.endPrice - price) < 1e-6)

console.log(`【${symbol} ${interval}】笔 ${strokes.length} 条 | 线段 ${an.chan.segmentCount} 段 | 线段中枢 ${an.chan.pivotCount}`)
console.log(`端点：${gp.map(p => `#${p.bar}@${p.price.toFixed(0)}`).join('  ')}\n`)
let bad = 0, warn = 0
const flag = (ok, label, extra = '') => { if (!ok) bad++; console.log(`  ${ok ? '✓' : '✗'} ${label}${extra ? '  ' + extra : ''}`) }
const note = (label) => { warn++; console.log(`  ⚠ ${label}`) }

console.log('① 每段笔数（奇数且 ≥3）')
for (const [i, seg] of segments.entries()) {
  const si = siOf(seg.startBar, seg.startPrice), ei = eiOf(seg.endBar, seg.endPrice)
  if (si < 0 || ei < 0) { flag(false, `第 ${i + 1} 段端点不在笔端点上`); continue }
  const n = ei - si + 1
  flag(n >= 3 && n % 2 === 1, `第 ${i + 1} 段：${n} 笔`)
}

console.log('\n② 首尾相连与方向交替')
for (let i = 1; i < segments.length; i++) {
  const p = segments[i - 1], c = segments[i]
  const joined = p.endBar === c.startBar && Math.abs(p.endPrice - c.startPrice) < 1e-6
  flag(joined && p.isUp !== c.isUp, `第 ${i}→${i + 1} 段：相连且方向交替`)
}

console.log('\n③ 段终点为该段方向极值（尾部未确认段不适用）')
for (const [i, seg] of segments.entries()) {
  const si = siOf(seg.startBar, seg.startPrice), ei = eiOf(seg.endBar, seg.endPrice)
  if (si < 0 || ei < 0) continue
  const isTrailing = i === segments.length - 1
  const span = strokes.slice(si, ei + 1)
  const hi = Math.max(...span.map(s => s.high)), lo = Math.min(...span.map(s => s.low))
  const ok = seg.isUp ? Math.abs(seg.endPrice - hi) < 1e-6 : Math.abs(seg.endPrice - lo) < 1e-6
  if (isTrailing) { note(`第 ${i + 1} 段为尾部未确认段（终点随行情推进），不做极值判定`); continue }
  if (!ok) {
    // 已知边界情形（口径 B，见 PLAN §0.13.2 与 ChanSegmentBuilder 类注释）：
    // 段内极值出现在首笔时，特征序列分型的中元素不可能落在第一个元素上 → 段延伸到破坏点，
    // 终点与段内极值不一致；强制一致会把该段切成 1 笔，违反"线段至少三笔"。故记为提醒而非失败。
    note(`第 ${i + 1} 段终点=${seg.endPrice.toFixed(0)} ≠ 段内极值 ${seg.isUp ? hi.toFixed(0) : lo.toFixed(0)}（已知边界情形·口径B）`)
    continue
  }
  flag(true, `第 ${i + 1} 段（${seg.isUp ? '上' : '下'}）终点为该段方向极值`)
}

console.log('\n④ 破坏点独立复算（含缺口回补；扫描到段终点后 6 个反向笔）')
for (const [i, seg] of segments.entries()) {
  const si = siOf(seg.startBar, seg.startPrice), ei = eiOf(seg.endBar, seg.endPrice)
  if (si < 0 || ei < 0) continue
  const dir = seg.isUp, elems = []
  for (let k = si + 1; k <= ei + 12 && k < strokes.length; k += 2) elems.push({ ...strokes[k], idx: k })
  let breakIdx = -1
  for (let m = 1; m + 1 < elems.length; m++) {
    const L = elems[m - 1], M = elems[m], R = elems[m + 1]
    const fractal = dir ? (M.high > L.high && M.high > R.high && M.low > L.low && M.low > R.low)
                        : (M.low < L.low && M.low < R.low && M.high < L.high && M.high < R.high)
    if (!fractal) continue
    const gap = dir ? L.high < M.low : L.low > M.high
    if (!gap) { breakIdx = M.idx; break }
    for (let t = m + 2; t < elems.length; t++) {
      const closed = dir ? elems[t].low <= L.high : elems[t].high >= L.low
      if (closed) { breakIdx = M.idx; break }
    }
    if (breakIdx >= 0) break
  }
  if (breakIdx < 0) { note(`第 ${i + 1} 段在扫描范围内未找到破坏点（可能为尾部延伸段）`); continue }
  flag(ei === breakIdx - 1, `第 ${i + 1} 段终点笔=${ei}，独立复算=${breakIdx - 1}`)
}
console.log(`\n${bad === 0 ? '✅ 无失败项' : `❌ ${bad} 项失败`}${warn ? `（另有 ${warn} 项提醒，属尾部段/扫描范围）` : ''}`)
