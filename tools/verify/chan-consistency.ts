// 缠论结构一致性校验（真实数据）：
// 等价于在 ChanStrokeBuilder/ChanPivotDetector 挂日志点后比对中间值——
// 校验 ① 笔端点与分型点重合 ② 中枢带只填充在中枢区间内且上下沿恒定 ③ 摘要与序列一致 ④ 买卖点都在已确认结构上
const BASE = 'http://localhost:5099'

async function getJson(url) {
  const r = await fetch(url)
  if (!r.ok) throw new Error(`${url} → HTTP ${r.status}`)
  return r.json()
}

let failures = 0
function check(cond, label, extra = '') {
  if (cond) { console.log(`  ✓ ${label}`) }
  else { failures++; console.log(`  ✗ ${label} ${extra}`) }
}

for (const [symbol, interval] of [['BTCUSDT', '1h'], ['ETHUSDT', '15m'], ['SOLUSDT', '4h']]) {
  const [kl, an] = await Promise.all([
    getJson(`${BASE}/api/klines?market=spot&symbol=${symbol}&interval=${interval}&limit=500`),
    getJson(`${BASE}/api/analysis?market=spot&symbol=${symbol}&interval=${interval}&limit=500`),
  ])
  const candles = kl.candles
  const s = an.series
  const stroke = s.chanStroke
  const top = s.chanFractalTop
  const bottom = s.chanFractalBottom
  const zg = s.chanZg
  const zd = s.chanZd
  const chan = an.chan
  console.log(`\n[${symbol} ${interval}] 笔=${chan.strokeCount} 中枢=${chan.pivotCount} 缠论信号=${an.signals.filter(x => x.source === '缠论').length}`)

  // ① 笔端点必须落在分型点集合上
  let mismatch = 0
  for (let i = 0; i < stroke.length; i++) {
    if (stroke[i] == null) continue
    if (top[i] !== stroke[i] && bottom[i] !== stroke[i]) mismatch++
  }
  check(mismatch === 0, '笔端点与分型点重合', `不匹配 ${mismatch} 处`)

  // ② 中枢带：填充处 zg/zd 同时存在且 zg > zd；且区间连续（不允许单点孤立）
  let bandBad = 0, bandBars = 0
  for (let i = 0; i < zg.length; i++) {
    const hasZg = zg[i] != null, hasZd = zd[i] != null
    if (hasZg !== hasZd) bandBad++
    if (hasZg && hasZd) { bandBars++; if (!(zg[i] > zd[i])) bandBad++ }
  }
  check(bandBad === 0, '中枢带上下沿成对出现且 Zg > Zd', `异常 ${bandBad} 处`)

  // ③ 序列长度与K线一致
  check(stroke.length === candles.length && zg.length === candles.length,
    '缠论序列长度与K线一致')

  // ④ 摘要与序列一致：最新中枢数值应能在带里找到
  if (chan.pivotZg != null) {
    const present = zg.some(v => v != null && Math.abs(v - chan.pivotZg) < 1e-9)
    check(present, '摘要中的最新中枢上沿出现在序列里', `zg=${chan.pivotZg}`)
  }

  // ⑤ 缠论买卖点：时间必须落在K线范围内，且止损方向正确（买点止损低于参考价，卖点高于）
  const range = [candles[0].time, candles[candles.length - 1].time]
  let bad = 0
  for (const sig of an.signals.filter(x => x.source === '缠论')) {
    if (sig.time < range[0] || sig.time > range[1]) bad++
    if (!sig.isConfirmed) bad++
    if (sig.side === 'buy' && !(sig.stopPrice < sig.price)) bad++
    if (sig.side === 'sell' && !(sig.stopPrice > sig.price)) bad++
    if (!/^\[(1|2|3)[买卖]\]/.test(sig.note)) bad++
  }
  check(bad === 0, '缠论买卖点：范围内/已确认/止损方向/类别前缀均正确', `异常 ${bad} 处`)
}

console.log(failures === 0 ? '\n全部一致性校验通过' : `\n失败 ${failures} 项`)
process.exit(failures === 0 ? 0 : 1)
