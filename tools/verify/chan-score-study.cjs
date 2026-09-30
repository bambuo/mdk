// 缠论「联合打分 v2」（按类别定义特征，等权 0–5）的**预注册**样本外检验。
//
// ── 预注册判据（v2 于 2026-09-30 写定，见 PLAN §0.19；v1 已判未通过，根因是顺势特征与反转语义相抵） ──
// 分数：JointScoreRules v2——按类别取特征集：
//   1/2 类（反转）：① 逆 EMA200 ② 强背驰（面积比≤0.7）③ RSI 极端（买≤35/卖≥65）④ 入场成本低（≤0.5R）⑤ 止损效率（≤2.5%）
//   3 类（延续）：　① 顺 EMA200 ② RSI 顺向（买≥50/卖≤50）③ MACD 柱同向 ④ 入场成本低（≤0.5R）⑤ 止损效率（≤2.5%）
// 分档：低 ≤2 / 中 3 / 高 ≥4。
// 分段：按信号K线时间排序，前 60% 选择段、后 40% 验证段；**两段都要**满足才算通过。
// 高分组通过门槛：独立波次≥30、平均超额 t≥2.5（分层比较，多重比较加严）、中位超额>0、
//                 扣费后为正≥50%、单一标的占比≤50%，且**高分组平均超额 > 低分组**（单调性）。
// 口径：**仅用回填样本**（origin=backfill，v2 重算、同口径可复现）；实时样本不掺入（打分口径在切换期不一致）。
//       波次去重（同标的/周期/方向、间隔≤24根算一波，取波首）。
// 用法：bun tools/verify/chan-score-study.cjs
const { load } = require('./signals-db.cjs')

const rows = load().filter(r =>
  r.source === '缠论' && r.outcome && r.outcome.status === 'ok' && r.jointScore !== null && r.origin === 'backfill')

const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN
const std = a => { const m = mean(a); return a.length > 1 ? Math.sqrt(mean(a.map(v => (v - m) ** 2))) : NaN }
const tstat = a => a.length > 1 ? mean(a) / (std(a) / Math.sqrt(a.length)) : NaN
const median = a => { if (!a.length) return NaN; const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)] }
const barSeconds = iv => ({ '15m': 900, '30m': 1800, '1h': 3600, '4h': 14400, '1d': 86400 })[iv] ?? 3600

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
    nRaw: items.length, nEps: eps.length, excess: mean(ex), median: median(ex), t: tstat(ex),
    netPositive: eps.length ? eps.filter(r => r.outcome.netPositive).length / eps.length : 0, topShare,
  }
}

const tiers = { 低: r => r.jointScore <= 2, 中: r => r.jointScore === 3, 高: r => r.jointScore >= 4 }
const passHigh = s => s.nEps >= 30 && s.t >= 2.5 && s.median > 0 && s.netPositive >= 0.5 && s.topShare <= 0.5
const fmt = d => new Date(d * 1000).toISOString().slice(0, 10)
const c = s => `n=${String(s.nEps).padStart(3)} 超额=${(100 * s.excess).toFixed(2)}% 中位=${(100 * s.median).toFixed(2)}% t=${s.t.toFixed(2)} 扣费正=${(100 * s.netPositive).toFixed(0)}% 集中=${(100 * s.topShare).toFixed(0)}%`

if (rows.length === 0) {
  console.log('带联合打分的缠论样本为 0：先跑一次历史回填（POST /api/backfill/run）补齐 joint_score，再运行本脚本。')
  process.exit(0)
}

const sorted = [...rows].sort((a, b) => a.time - b.time)
const splitAt = Math.floor(sorted.length * 0.6)
const train = sorted.slice(0, splitAt)
const test = sorted.slice(splitAt)

const dist = { 0: 0, 1: 0, 2: 0, 3: 0, 4: 0, 5: 0 }
for (const r of rows) dist[r.jointScore]++
console.log(`样本：缠论已评估且带打分 ${rows.length} 条（另有 ${load().filter(r => r.source === '缠论' && r.outcome?.status === 'ok' && r.jointScore === null).length} 条未打分）`)
console.log(`分段：选择段 ${fmt(train[0].time)} ~ ${fmt(train.at(-1).time)}（${train.length} 条）｜验证段 ${fmt(test[0].time)} ~ ${fmt(test.at(-1).time)}（${test.length} 条）`)
console.log(`分数分布：${Object.entries(dist).map(([k, v]) => `${k}分:${v}`).join('  ')}`)
console.log('门槛：高分组两段都要 波次≥30 · t≥2.5 · 中位>0 · 扣费正≥50% · 集中≤50%，且高分组>低分组（单调性）')

console.log('\n===== 基线（全部打分样本）=====')
console.log(`  选择段 ${c(stats(train))}`)
console.log(`  验证段 ${c(stats(test))}`)

const tr = {}, te = {}
for (const [name, pred] of Object.entries(tiers)) {
  tr[name] = stats(train.filter(pred))
  te[name] = stats(test.filter(pred))
  console.log(`\n===== ${name}分组（≤2 / 3 / ≥4）=====`)
  console.log(`  选择段 ${c(tr[name])}`)
  console.log(`  验证段 ${c(te[name])}`)
}

const mono = tr['高'].excess > tr['低'].excess && te['高'].excess > te['低'].excess
const ok = passHigh(tr['高']) && passHigh(te['高']) && mono
console.log('\n===== 结论 =====')
if (ok) {
  console.log('通过：高分组在两段都达标且优于低分组——可按"已通过样本外检验"展示，但仍受限于历史样本口径。')
} else {
  const reasons = []
  if (!passHigh(tr['高'])) reasons.push('选择段高分组未达标')
  if (!passHigh(te['高'])) reasons.push('验证段高分组未达标')
  if (!mono) reasons.push('分组单调性不成立（高分未优于低分）')
  console.log(`未通过：${reasons.join('；')}。按纪律联合打分**只作事实聚合展示，不启用"可实盘"标签**。`)
}
