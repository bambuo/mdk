// 检验"笔中枢"的尺度特性：若三个周期各自都是"笔中枢"，其区间宽度（%）应随周期增大而增大（波动按时间尺度增长）
// 若不同周期的中枢宽度（%）接近或倒挂，说明"同一周期笔中枢"并不对应规范级别
async function main() {
  const symbols = ['BTCUSDT', 'ETHUSDT', 'SOLUSDT', 'BNBUSDT', 'XRPUSDT']
  const intervals = ['15m', '1h', '4h']
  const rows = {}
  for (const itv of intervals) {
    const widths = []
    const strokes = []
    for (const sym of symbols) {
      const d = await fetch(`http://localhost:5099/api/analysis?market=spot&symbol=${sym}&interval=${itv}&limit=500`).then(r => r.json())
      for (const p of d.chan.pivots) widths.push((p.zg - p.zd) / ((p.zg + p.zd) / 2) * 100)
      for (const p of d.chan.pivots) strokes.push(p.strokes)
    }
    widths.sort((a, b) => a - b); strokes.sort((a, b) => a - b)
    rows[itv] = {
      n: widths.length,
      中枢宽度中位: widths.length ? widths[Math.floor(widths.length / 2)].toFixed(3) + '%' : 'n/a',
      中枢成笔数中位: strokes.length ? strokes[Math.floor(strokes.length / 2)] : 0,
    }
  }
  console.log('同一标的、同一时间跨度（500 根K线）下各周期的"笔中枢"特征：')
  console.table(rows)
  console.log('说明：若 15m/1h/4h 的中枢宽度（%）应按 √时间 比例增长（约 1 : 2 : 4），')
  console.log('      而实测接近或倒挂，则说明"每个周期各自算笔中枢"并不构成规范意义上的相邻级别。')
}
main()
