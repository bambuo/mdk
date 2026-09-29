// 用接口返回的笔端点重建笔序列，再按特征序列规则独立判断"第 3 段是否应该被破坏"
const d = await fetch('http://localhost:5099/api/analysis?market=spot&symbol=BTCUSDT&interval=1h&limit=700&segments=true').then(r => r.json())
const kl = await fetch('http://localhost:5099/api/klines?market=spot&symbol=BTCUSDT&interval=1h&limit=700').then(r => r.json())
const strokeSeries = d.series.chanStroke
const pts = []
for (let i = 0; i < strokeSeries.length; i++) if (strokeSeries[i] != null) pts.push({ bar: i, price: strokeSeries[i] })
const strokes = []
for (let i = 1; i < pts.length; i++) {
  strokes.push({ from: pts[i-1], to: pts[i], isUp: pts[i].price > pts[i-1].price,
                 high: Math.max(pts[i-1].price, pts[i].price), low: Math.min(pts[i-1].price, pts[i].price) })
}
console.log(`笔数=${strokes.length}`)

// 第三段起点 = 价格 77620 所在端点（bar 194）
const startIdx = strokes.findIndex(s => Math.abs(s.from.price - 77620.01) < 1)
console.log(`第三段起点笔序号=${startIdx}（价格 ${strokes[startIdx]?.from.price}）`)

// 特征序列 = 该段内的反向笔
const dir = strokes[startIdx].isUp
const elems = []
for (let k = startIdx + 1; k < strokes.length; k += 2) elems.push({ ...strokes[k], idx: k })
console.log(`特征序列元素数=${elems.length}（每元素=一次反向回调）`)
console.log('回调明细（起点价→终点价 / 区间）:')
elems.forEach((e, i) => {
  const prev = i > 0 ? elems[i-1] : null
  const nxt = i + 1 < elems.length ? elems[i+1] : null
  const isFractal = prev && nxt && e.high > prev.high && e.high > nxt.high && e.low > prev.low && e.low > nxt.low
  console.log(`  #${i} bar${e.from.bar}→${e.to.bar} [${e.low.toFixed(0)}, ${e.high.toFixed(0)}]${isFractal ? '   ← 满足顶分型（中间元素最高）' : ''}`)
})
const fractals = elems.filter((e, i) => i > 0 && i + 1 < elems.length &&
  e.high > elems[i-1].high && e.high > elems[i+1].high && e.low > elems[i-1].low && e.low > elems[i+1].low)
console.log(`\n独立复算：该段内满足"顶分型"的回调数 = ${fractals.length}`)
console.log(fractals.length === 0 ? '→ 确实没有破坏点（线段延伸是数据本身的性质）' : '→ 存在破坏点但实现未确认 → 实现有 bug')
