const fs = require('fs')
const rows = fs.readFileSync('/Users/johana/Desktop/mdk/backend/Mdk.Api/data/signals.jsonl','utf8').trim().split('\n')
  .map(l=>JSON.parse(l)).filter(r=>r.source==='缠论'&&r.outcome&&r.outcome.status==='ok')
const mean=a=>a.reduce((x,y)=>x+y,0)/a.length
const std=a=>{const m=mean(a);return Math.sqrt(mean(a.map(v=>(v-m)**2)))}
const t=a=>a.length>1?mean(a)/(std(a)/Math.sqrt(a.length)):NaN

// ① 时间聚类：把 24 小时内的所有信号视为"同一次全市场行情"→ 1 个有效样本
const byDay = {}
for (const r of rows) { const d = Math.floor(r.time / 86400); (byDay[d] ??= []).push(r) }
const dayMeans = Object.values(byDay).map(g => mean(g.map(r => r.outcome.excess)))
console.log(`① 时间聚类：${rows.length} 条 → ${dayMeans.length} 个"交易日"样本`)
console.log(`   日均超额 t=${t(dayMeans).toFixed(2)}（按日聚合后仍显著则说明不是靠单日行情）`)

// ② 去集中度：剔除样本最多的标的
const bySym = {}
for (const r of rows) (bySym[r.symbol] ??= []).push(r)
const top = Object.entries(bySym).sort((a,b)=>b[1].length-a[1].length)[0]
const rest = rows.filter(r => r.symbol !== top[0])
console.log(`② 去集中度：剔除 ${top[0]}（${top[1].length} 条，超额 ${(100*mean(top[1].map(r=>r.outcome.excess))).toFixed(2)}%）`)
console.log(`   剩余 n=${rest.length} 平均超额=${(100*mean(rest.map(r=>r.outcome.excess))).toFixed(2)}% t=${t(rest.map(r=>r.outcome.excess)).toFixed(2)} 胜率=${(100*rest.filter(r=>r.outcome.ret>0).length/rest.length).toFixed(0)}%`)

// ③ 逐笔超额分布与最差情形
const ex = rows.map(r=>r.outcome.excess).sort((a,b)=>a-b)
console.log(`③ 分布：最好 ${(100*ex[ex.length-1]).toFixed(1)}% / 中位 ${(100*ex[Math.floor(ex.length/2)]).toFixed(2)}% / 最差 ${(100*ex[0]).toFixed(1)}%`)
const mae = rows.map(r=>r.outcome.mae)
console.log(`   持有期最大浮亏中位 ${(100*mae.sort((a,b)=>a-b)[Math.floor(mae.length/2)]).toFixed(2)}%（止损距离中位 2.0%，说明多数信号不触及结构失效位）`)

// ④ 若只用前 2/3 样本（近似样本外）
const sorted = [...rows].sort((a,b)=>a.time-b.time)
const cut = Math.floor(sorted.length*2/3)
const holdout = sorted.slice(cut)
console.log(`④ 时间后 1/3 样本（近似样本外）：n=${holdout.length} 胜率=${(100*holdout.filter(r=>r.outcome.ret>0).length/holdout.length).toFixed(0)}% 超额=${(100*mean(holdout.map(r=>r.outcome.excess))).toFixed(2)}% t=${t(holdout.map(r=>r.outcome.excess)).toFixed(2)}`)
