// 信号台账读取器（供校验脚本共用）：直接读 SQLite（data/signals.db），
// 还原成脚本一直使用的行结构（symbol 连写成 BASEQUOTE、outcome 为嵌套对象、布尔为 true/false）。
//
// 为什么要还原成旧结构：这些脚本的统计逻辑（波次去重、t 值、分组）与历史结论必须可比，
// 换存储不应改变统计口径；字段名与语义保持不变，只有数据来源从 JSONL 换成 SQLite。
//
// 标的在库里是 base_asset / quote_asset 两个独立列（与后端交易对值对象口径一致），
// 这里按需拼回连写串供分组使用；判断"是谁"请优先用 base/quote 两个字段。
const { Database } = require('bun:sqlite')

const DEFAULT_DB = '/Users/johana/Desktop/mdk/backend/Mdk.Api/data/signals.db'

/** TEXT 存的小数 → JS 数值（缺失/空串 → null）。 */
function num(value) {
  return value === null || value === undefined || value === '' ? null : Number(value)
}

function ratio(numerator, denominator) {
  if (numerator === null || !denominator) return null
  return numerator / denominator
}

function bool(value) {
  return value === null || value === undefined ? null : value === 1
}

/**
 * 读取台账全部行。
 * @param {string} [dbPath] 数据库路径，默认 backend/Mdk.Api/data/signals.db
 */
function load(dbPath) {
  const db = new Database(dbPath ?? DEFAULT_DB, { readonly: true })
  try {
    return db.query(`
      SELECT market, base_asset, quote_asset, interval, source, kind, side, signal_time AS time,
             is_confirmed, price, stop_price, reference_price, note, confluence, adx,
             atr_pct, bandwidth_pct, joint_score, origin, recorded_at,
             outcome_status, outcome_ret, outcome_excess, outcome_mfe, outcome_mae,
             outcome_stop_hit, outcome_net_positive, outcome_evaluated_at
      FROM signals
      ORDER BY signal_time
    `).all().map(row => ({
      market: row.market,
      baseAsset: row.base_asset,
      quoteAsset: row.quote_asset,
      symbol: row.base_asset + row.quote_asset,
      display: `${row.base_asset}/${row.quote_asset}`,
      interval: row.interval,
      source: row.source,
      kind: row.kind,
      side: row.side,
      time: row.time,
      isConfirmed: row.is_confirmed === 1,
      price: num(row.price),
      stopPrice: num(row.stop_price),
      referencePrice: num(row.reference_price),
      // 风险口径（与后端 TradeSignal 的 RiskPct / EntryLagPct / LagShare 同定义）
      riskPct: ratio(num(row.stop_price) === null ? null : Math.abs(num(row.price) - num(row.stop_price)), num(row.price)),
      entryLagPct: ratio(num(row.reference_price) === null ? null : Math.abs(num(row.price) - num(row.reference_price)), num(row.price)),
      note: row.note,
      // trendAligned 维度已于 2026-09-30 全链路移除（列已删）：恒为 null，
      // 依赖它的研究脚本（§0.11 已判负结果）不再可用，保留键位只为让脚本能跑完并报"样本为 0"
      trendAligned: null,
      confluence: row.confluence,
      adx: num(row.adx),
      atrPct: num(row.atr_pct),
      bandwidthPct: num(row.bandwidth_pct),
      /** 联合打分（结构共振 + 技术指标，等权 0–6）；旧行/未回填为 null */
      jointScore: row.joint_score === null || row.joint_score === undefined ? null : Number(row.joint_score),
      origin: row.origin,
      recordedAt: row.recorded_at,
      get lagShare() {
        return this.riskPct && this.riskPct > 0 && this.entryLagPct !== null
          ? this.entryLagPct / this.riskPct
          : null
      },
      outcome: row.outcome_status === null ? null : {
        status: row.outcome_status,
        ret: num(row.outcome_ret),
        excess: num(row.outcome_excess),
        mfe: num(row.outcome_mfe),
        mae: num(row.outcome_mae),
        stopHit: bool(row.outcome_stop_hit),
        netPositive: bool(row.outcome_net_positive),
        evaluatedAt: row.outcome_evaluated_at,
      },
    }))
  } finally {
    db.close()
  }
}

module.exports = { load, DEFAULT_DB }
