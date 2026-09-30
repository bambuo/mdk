// 未挖窗口（2026-01-01 → 06-30）上的**样本外复现检验**：回放能否替代"等实时样本"。
//
// 背景（用户提议）：不必等几周实时样本，用历史回放把样本量堆到门槛以上。
// 前提：回放样本必须是**从未被任何研究碰过**的窗口——已挖窗口（2026-07-01 起）上跑过
// 13 个门控、打分 v1/v2、共振/次级别、持有期探测，再用它自证就是多重比较陷阱（见 PLAN §0.21）。
//
// 本脚本因此固定两件事：
//   ① 只读 origin=backfill 且 signal_time < 2026-07-01 的样本（未挖窗口）；
//   ② 判据在跑数据前写定（与实盘晋升判据同 7 条，样本量门槛按"独立波次 ≥100"）。
// 判据：① 独立波次 ≥100 ② 超额 t ≥2 ③ 中位超额 >0 ④ 扣费后为正 ≥55%
//       ⑤ 时间前后分半均值都 >0 ⑥ 单一标的 ≤40% ⑦ MAE 中位 ≤1R
// 用法：bun tools/verify/chan-fresh-window-study.cjs
const { load } = require('./signals-db.cjs')

const CUTOFF = Date.UTC(2026, 6, 1) / 1000   // 2026-07-01 00:00 UTC：已挖窗口的起点
const rows = load().filter(r => r.source === '缠论' && r.outcome?.status === 'ok' && r.origin === 'backfill')

const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN
const std = a => { const m = mean(a); return a.length > 1 ? Math.sqrt(mean(a.map(v => (v - m) ** 2))) : NaN }
const tstat = a => a.length > 1 ? mean(a) / (std(a) / Math.sqrt(a.length)) : NaN
const median = a => { if (!a.length) return NaN; const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)] }
const barSec = iv => ({ '15m': 900, '30m': 1800, '1h': 3600, '4h': 14400, '1d': 86400 })[iv] ?? 3600
const pct = v => `${(100 * v).toFixed(2)}%`
const day = t => new Date(t * 1000).toISOString().slice(0, 10)

function episodes(items) {
  const out = []
  for (const key of new Set(items.map(r => `${r.symbol}|${r.interval}|${r.side}`))) {
    const g = items.filter(r => `${r.symbol}|${r.interval}|${r.side}` === key).sort((a, b) => a.time - b.time)
    const bar = barSec(g[0].interval)
    let prev = null
    for (const r of g) { if (prev === null || r.time - prev > bar * 24) out.push(r); prev = r.time }
  }
  return out
}

function stats(items) {
  const eps = episodes(items)
  const ex = eps.map(r => r.outcome.excess)
  const rowsR = eps.filter(r => r.riskPct > 0 && r.outcome.mae != null)
  return {
    n: items.length, wave: eps.length,
    win: eps.length ? eps.filter(r => r.outcome.ret > 0).length / eps.length : NaN,
    net: eps.length ? eps.filter(r => r.outcome.netPositive).length / eps.length : NaN,
    excess: mean(ex), median: median(ex), t: tstat(ex),
    top: eps.length ? Math.max(...[...new Set(eps.map(r => r.symbol))].map(s => eps.filter(r => r.symbol === s).length)) / eps.length : NaN,
    maeR: median(rowsR.map(r => Math.abs(r.outcome.mae) / r.riskPct)),
  }
}

/** 预注册判据（与 SignalQualityRules.TradableGate 同 7 条；样本量按独立波次 ≥100） */
function judge(s, items) {
  const fails = []
  if (s.wave < 100) fails.push(`独立波次 ${s.wave} < 100`)
  if (!(s.t >= 2)) fails.push(`t=${s.t.toFixed(2)} < 2`)
  if (!(s.median > 0)) fails.push(`中位超额 ${pct(s.median)} ≤ 0`)
  if (!(s.net >= 0.55)) fails.push(`扣费后为正 ${(100 * s.net).toFixed(0)}% < 55%`)
  if (!(s.top <= 0.40)) fails.push(`单一标的 ${(100 * s.top).toFixed(0)}% > 40%`)
  if (!(s.maeR <= 1)) fails.push(`MAE 中位 ${s.maeR.toFixed(2)}R > 1R`)
  const eps = episodes(items).sort((a, b) => a.time - b.time)
  const half = Math.floor(eps.length / 2)
  const first = mean(eps.slice(0, half).map(r => r.outcome.excess))
  const second = mean(eps.slice(half).map(r => r.outcome.excess))
  if (!(first > 0 && second > 0)) fails.push(`时间分半不稳定（前半 ${pct(first)} / 后半 ${pct(second)}）`)
  return { pass: fails.length === 0, fails, first, second }
}

const fresh = rows.filter(r => r.time < CUTOFF)
const mined = rows.filter(r => r.time >= CUTOFF)

console.log(`未挖窗口样本 ${fresh.length} 条（${day(Math.min(...fresh.map(r => r.time)))} ~ ${day(Math.max(...fresh.map(r => r.time)))}）`)
console.log(`已挖窗口样本 ${mined.length} 条（仅作对照，不得用于自证）`)
console.log(`判据（先于数据写定）：波次≥100 · t≥2 · 中位>0 · 扣费后为正≥55% · 分半都为正 · 单标的≤40% · MAE≤1R\n`)

const fs_ = stats(fresh), ms = stats(mined)
const line = (tag, s) => console.log(`  ${tag} 原始=${String(s.n).padStart(5)} 波次=${String(s.wave).padStart(4)} 胜率=${(100 * s.win).toFixed(0)}% 扣费正=${(100 * s.net).toFixed(0)}% 均超额=${pct(s.excess)} 中位=${pct(s.median)} t=${s.t.toFixed(2)} 单标的=${(100 * s.top).toFixed(0)}% MAE=${s.maeR.toFixed(2)}R`)
console.log('===== 全样本对照 =====')
line('未挖窗口', fs_)
line('已挖窗口', ms)

console.log('\n===== 未挖窗口 · 按类别 =====')
const kinds = ['1买', '2买', '3买', '1卖', '2卖', '3卖']
for (const k of kinds) {
  const g = fresh.filter(r => r.kind === k)
  if (!g.length) continue
  const s = stats(g)
  if (s.wave < 10) { console.log(`  ${k} 波次过少（${s.wave}），跳过`); continue }
  const v = judge(s, g)
  console.log(`  ${k} 波次=${s.wave} 胜率=${(100 * s.win).toFixed(0)}% 扣费正=${(100 * s.net).toFixed(0)}% 中位=${pct(s.median)} t=${s.t.toFixed(2)} → ${v.pass ? '★通过判据' : '未通过（' + v.fails[0] + '）'}`)
}

console.log('\n===== 未挖窗口 · 按周期 =====')
for (const iv of ['1h', '4h']) {
  const g = fresh.filter(r => r.interval === iv)
  const s = stats(g)
  const v = judge(s, g)
  console.log(`  ${iv} 波次=${s.wave} 胜率=${(100 * s.win).toFixed(0)}% 扣费正=${(100 * s.net).toFixed(0)}% 中位=${pct(s.median)} t=${s.t.toFixed(2)} → ${v.pass ? '★通过判据' : '未通过（' + v.fails[0] + '）'}`)
}

console.log('\n===== 已挖窗口上"看起来有机会"的格子，在未挖窗口是否复现 =====')
const replicate = (label, pick) => {
  const a = stats(mined.filter(pick)), b = stats(fresh.filter(pick))
  console.log(`  ${label}`)
  console.log(`    已挖：波次=${a.wave} 胜率=${(100 * a.win).toFixed(0)}% 中位=${pct(a.median)} t=${a.t.toFixed(2)}`)
  console.log(`    未挖：波次=${b.wave} 胜率=${(100 * b.win).toFixed(0)}% 中位=${pct(b.median)} t=${b.t.toFixed(2)} → ${b.wave < 30 ? '样本不足' : (b.t >= 2 && b.median > 0 ? '复现（值得进一步验证）' : '未复现（已挖窗口的格子是样本内噪声）')}`)
}
replicate('4h 周期', r => r.interval === '4h')
replicate('3买', r => r.kind === '3买')
replicate('3卖', r => r.kind === '3卖')
replicate('1买', r => r.kind === '1买')
replicate('顺高周期均线（trendAligned=true）', r => r.trendAligned === true)
replicate('强背驰（面积比 ≤0.7）', r => (r.note || '').match(/背驰 (\d\.\d+)/) && parseFloat((r.note || '').match(/背驰 (\d\.\d+)/)[1]) <= 0.7)

console.log('\n===== 结论 =====')
const overall = judge(fs_, fresh)
console.log(overall.pass
  ? `未挖窗口上**通过全部 7 条预注册判据**（波次 ${fs_.wave}，t=${fs_.t.toFixed(2)}，中位 ${pct(fs_.median)}）——回放样本可替代"等实时"作为晋升证据，但仅限该冻结规则与窗口。`
  : `未挖窗口上**未通过**：${overall.fails.join('；')}。即回放不能替代实时证据——不是样本量不够，而是这套规则在样本外依旧没有可辨优势。`)
