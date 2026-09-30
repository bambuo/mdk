// ⚠ 注意：脚本内“顺大势（高周期同向）”门控依赖 trendAligned 维度，该维度已移除 → 该门控恒为 0 样本，
//    其余 12 个门控不受影响。
// 缠论信号「可交易性门控」研究：预注册判据 + 时间前段选、后段验（walk-forward）。
//
// ── 预注册判据（在跑数据之前写定，避免事后挑剔） ──
// 分段：按信号 K 线时间排序，前 60% 为选择段，后 40% 为验证段。门控必须在**两段都达标**才算通过。
// 口径：波次去重（同标的/周期/方向，间隔 ≤24 根 K 线算一波，取波首），与项目既有统计纪律一致。
// 每段门槛：① 独立波次 ≥ 30 ② 平均超额 t ≥ 2 ③ 中位超额 > 0 ④ 扣费后为正 ≥ 50% ⑤ 单一标的占比 ≤ 50%
// 对照：同时给出"无门控基线"；门控只有在两段都不劣于基线时才有意义。
// 多重比较：本脚本一次检验的门控数量会打印出来；若要看"最佳门控"，门槛提高到 t ≥ 2.5。
//
// 门控变量都在**信号时点**可知（入场价、结构参考价、止损位、ADX、趋势对齐、周期、类别），不含未来数据。
//
// 用法：bun tools/verify/chan-gate-study.cjs
const { load } = require('./signals-db.cjs')

const rows = load().filter(r =>
  r.source === '缠论' && r.outcome && r.outcome.status === 'ok')

const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN
const std = a => { const m = mean(a); return a.length > 1 ? Math.sqrt(mean(a.map(v => (v - m) ** 2))) : NaN }
const tstat = a => a.length > 1 ? mean(a) / (std(a) / Math.sqrt(a.length)) : NaN
const median = a => { if (!a.length) return NaN; const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)] }
const barSeconds = iv => ({ '15m': 900, '30m': 1800, '1h': 3600, '4h': 14400, '1d': 86400 })[iv] ?? 3600

/** 波次去重：同标的/周期/方向，间隔 ≤24 根归为一波，取波内首条。 */
function episodes(items) {
  const out = []
  for (const key of new Set(items.map(r => `${r.symbol}|${r.interval}|${r.side}`))) {
    const g = items.filter(r => `${r.symbol}|${r.interval}|${r.side}` === key).sort((a, b) => a.time - b.time)
    const bar = barSeconds(g[0].interval)
    let prev = null
    for (const r of g) { if (prev === null || r.time - prev > bar * 24) out.push(r); prev = r.time }
  }
  return out
}

function stats(items) {
  const eps = episodes(items)
  const ex = eps.map(r => r.outcome.excess)
  const topShare = eps.length
    ? Math.max(...[...new Set(eps.map(r => r.symbol))].map(s => eps.filter(r => r.symbol === s).length)) / eps.length
    : 0
  return {
    nRaw: items.length,
    nEps: eps.length,
    excess: mean(ex),
    median: median(ex),
    t: tstat(ex),
    netPositive: eps.length ? eps.filter(r => r.outcome.netPositive).length / eps.length : 0,
    topShare,
    // R 口径：超额 ÷ 该信号的风险单位（不同标的/周期可比）
    excessR: mean(eps.filter(r => r.riskPct > 0).map(r => r.outcome.excess / r.riskPct)),
  }
}

const pass = s => s.nEps >= 30 && s.t >= 2 && s.median > 0 && s.netPositive >= 0.5 && s.topShare <= 0.5
const verdict = (train, test) =>
  pass(train) && pass(test) ? '通过'
    : pass(train) ? '未通过（样本外失效）'
      : pass(test) ? '未通过（选择段不达标，疑为偶然）'
        : '未通过'

// ── 门控定义（每个都是信号时点可算的谓词） ──
const hasLag = r => r.lagShare !== null
const gates = [
  ['入场质量：已走 ≤ 0.5R', r => hasLag(r) && r.lagShare <= 0.5],
  ['入场质量：已走 ≤ 0.75R', r => hasLag(r) && r.lagShare <= 0.75],
  ['入场质量：已走 ≤ 1R', r => hasLag(r) && r.lagShare <= 1.0],
  ['止损距离 ≤ 2%', r => r.riskPct !== null && r.riskPct <= 0.02],
  ['止损距离 ≤ 3%', r => r.riskPct !== null && r.riskPct <= 0.03],
  ['趋势市 ADX ≥ 25', r => r.adx !== null && r.adx >= 25],
  ['顺大势（高周期同向）', r => r.trendAligned === true],
  ['排除 2 类买卖点', r => !/^\[2/.test(r.note ?? '')],
  ['仅 3 类买卖点', r => /^\[3/.test(r.note ?? '')],
  ['仅 4h 周期', r => r.interval === '4h'],
  ['组合：已走 ≤0.75R 且 ADX ≥ 20', r => hasLag(r) && r.lagShare <= 0.75 && r.adx !== null && r.adx >= 20],
  ['组合：已走 ≤0.75R 且顺大势', r => hasLag(r) && r.lagShare <= 0.75 && r.trendAligned === true],
  ['组合：4h 且已走 ≤0.75R', r => r.interval === '4h' && hasLag(r) && r.lagShare <= 0.75],
]

const sorted = [...rows].sort((a, b) => a.time - b.time)
const splitAt = Math.floor(sorted.length * 0.6)
const train = sorted.slice(0, splitAt)
const test = sorted.slice(splitAt)
const fmt = (d) => new Date(d * 1000).toISOString().slice(0, 10)

console.log(`样本：缠论已评估 ${rows.length} 条（其中含结构参考价 ${rows.filter(hasLag).length} 条）`)
console.log(`分段：选择段 ${fmt(train[0].time)} ~ ${fmt(train.at(-1).time)}（${train.length} 条）｜验证段 ${fmt(test[0].time)} ~ ${fmt(test.at(-1).time)}（${test.length} 条）`)
console.log(`门控数量 ${gates.length}（多重比较：最佳门控需 t ≥ 2.5 才视为可启用）`)
console.log('\n风险口径分布（含参考价样本，波次去重前）:')
const withLag = rows.filter(hasLag)
console.log(`  入场已走幅度 ÷ 风险单位：中位 ${median(withLag.map(r => r.lagShare)).toFixed(2)}R` +
  `｜≤0.5R 占 ${(100 * withLag.filter(r => r.lagShare <= 0.5).length / withLag.length).toFixed(0)}%` +
  `｜≤1R 占 ${(100 * withLag.filter(r => r.lagShare <= 1).length / withLag.length).toFixed(0)}%`)
console.log(`  止损距离：中位 ${(100 * median(withLag.map(r => r.riskPct))).toFixed(2)}%`)

function line(label, trainS, testS, mark) {
  const c = (s) => `n=${String(s.nEps).padStart(3)} 超额=${(100 * s.excess).toFixed(2)}% 中位=${(100 * s.median).toFixed(2)}% t=${s.t.toFixed(2)} 扣费正=${(100 * s.netPositive).toFixed(0)}% 集中=${(100 * s.topShare).toFixed(0)}% ${(s.excessR).toFixed(2)}R`
  console.log(`  ${label.padEnd(30)} 选择段 ${c(trainS)} ｜ 验证段 ${c(testS)} → ${mark}`)
}

console.log('\n===== 基线（不加门控）=====')
line('全部信号', stats(train), stats(test), '对照')

console.log('\n===== 各门控（选择段 / 验证段）=====')
const results = []
for (const [label, predicate] of gates) {
  const tr = stats(train.filter(predicate))
  const te = stats(test.filter(predicate))
  const v = verdict(tr, te)
  results.push({ label, v, tr, te })
  line(label, tr, te, v)
}

const passed = results.filter(r => r.v === '通过')
console.log('\n===== 结论 =====')
if (passed.length === 0) {
  console.log('无门控通过预注册判据（两段都达标）——按纪律不启用任何门控；把入场质量与止损距离作为“事实提示”而非筛选条件。')
} else {
  for (const r of passed) {
    const strong = r.tr.t >= 2.5 && r.te.t >= 2.5
    console.log(`通过：${r.label}（选择段 t=${r.tr.t.toFixed(2)}，验证段 t=${r.te.t.toFixed(2)}）${strong ? '，且达到多重比较门槛 t≥2.5' : '，但未达多重比较门槛 t≥2.5，不足以单独启用'}`)
  }
}
