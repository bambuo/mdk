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
      SELECT market, base_asset, quote_asset, interval, source, side, signal_time AS time,
             is_confirmed, price, stop_price, note, trend_aligned, confluence, adx, atr_pct,
             bandwidth_pct, origin, recorded_at,
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
      side: row.side,
      time: row.time,
      isConfirmed: row.is_confirmed === 1,
      price: num(row.price),
      stopPrice: num(row.stop_price),
      note: row.note,
      trendAligned: bool(row.trend_aligned),
      confluence: row.confluence,
      adx: num(row.adx),
      atrPct: num(row.atr_pct),
      bandwidthPct: num(row.bandwidth_pct),
      origin: row.origin,
      recordedAt: row.recorded_at,
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
