<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  CandlestickSeries,
  ColorType,
  CrosshairMode,
  HistogramSeries,
  LineSeries,
  LineStyle,
  createChart,
  createSeriesMarkers,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type SeriesMarker,
  type ISeriesMarkersPluginApi,
  type ISeriesPrimitive,
  type IPrimitivePaneView,
  type IPrimitivePaneRenderer,
  type SeriesAttachedParameter,
  type Time,
  type UTCTimestamp,
} from 'lightweight-charts'
import type { AnalysisResult, Candle, Toggles } from '../types'
import { formatTime, intervalSeconds, pricePrecision } from '../utils'

const props = defineProps<{
  candles: Candle[]
  analysis: AnalysisResult | null
  toggles: Toggles
  symbol: string
  interval: string
}>()

const emit = defineEmits<{
  (e: 'stale'): void
}>()

const container = ref<HTMLElement | null>(null)

const COLORS = {
  up: '#26a69a',
  down: '#ef5350',
  ema20: '#f0b90b',
  ema50: '#5b8def',
  ema200: '#b388ff',
  boll: 'rgba(91,141,239,0.45)',
  bollMid: 'rgba(91,141,239,0.8)',
  rsi: '#d0a9ff',
  dif: '#f0b90b',
  dea: '#5b8def',
}

/**
 * 中枢色带：以半透明矩形填充 [Zd, Zg] 区间（轻量图表无原生色带，用 pane 自绘 primitive 实现）。
 * 这是"以缠论结构为主"的主要视觉载体——中枢是缠论的核心结构。
 */
interface PivotBand {
  from: number
  to: number
  zg: number
  zd: number
  confirmed: boolean
  /** higher=高周期（宽区间）/ primary=本级别 / lower=次级别（细粒度） */
  level: 'higher' | 'primary' | 'lower'
}

class PivotBandRenderer implements IPrimitivePaneRenderer {
  constructor(
    private bands: PivotBand[],
    private series: () => ISeriesApi<'Candlestick'> | null,
    private chart: () => IChartApi | null,
  ) {}

  draw(target: Parameters<IPrimitivePaneRenderer['draw']>[0]): void {
    const series = this.series()
    const chart = this.chart()
    if (!series || !chart) return
    target.useMediaCoordinateSpace(scope => {
      const ctx = scope.context
      const timeScale = chart.timeScale()
      const primaries = this.bands.filter(b => b.level === 'primary')
      const lastPrimary = primaries.length ? primaries[primaries.length - 1] : null
      this.bands.forEach(b => {
        const x1 = timeScale.timeToCoordinate(b.from as UTCTimestamp)
        const x2 = timeScale.timeToCoordinate(b.to as UTCTimestamp)
        const yTop = series.priceToCoordinate(b.zg)
        const yBot = series.priceToCoordinate(b.zd)
        if (x1 == null || x2 == null || yTop == null || yBot == null) return
        const isLatest = b.level === 'primary' && b === lastPrimary
        const left = Math.min(x1, x2)
        const width = Math.max(2, Math.abs(x2 - x1))
        const top = Math.min(yTop, yBot)
        const height = Math.max(2, Math.abs(yBot - yTop))
        // 按级别分色：高周期=暖橙（大区间，淡）、本级别=紫（主体）、次级别=蓝（细粒度，更淡）
        const palette = b.level === 'higher'
          ? { fill: 'rgba(240,160,80,0.07)', stroke: 'rgba(240,160,80,0.35)', dash: [8, 4] }
          : b.level === 'lower'
            ? { fill: 'rgba(90,170,240,0.08)', stroke: 'rgba(90,170,240,0.35)', dash: [3, 3] }
            : { fill: b.confirmed ? 'rgba(198,120,221,0.15)' : 'rgba(198,120,221,0.09)', stroke: 'rgba(198,120,221,0.5)', dash: [4, 3] }
        const fill = isLatest
          ? b.confirmed ? 'rgba(198,120,221,0.26)' : 'rgba(198,120,221,0.16)'
          : palette.fill
        const stroke = isLatest
          ? b.confirmed ? 'rgba(214,150,235,0.95)' : 'rgba(214,150,235,0.6)'
          : palette.stroke
        ctx.fillStyle = fill
        ctx.fillRect(left, top, width, height)
        ctx.strokeStyle = stroke
        ctx.lineWidth = isLatest ? 1.5 : 1
        ctx.setLineDash(isLatest ? [] : palette.dash)
        ctx.strokeRect(left, top, width, height)   // 四边封闭，读作"箱体"
        ctx.setLineDash([])
        if (isLatest) {
          ctx.font = '10px sans-serif'
          ctx.fillStyle = 'rgba(226,180,240,0.95)'
          ctx.fillText('中枢', left + 4, top - 4)
        }
      })
    })
  }
}

class PivotBandPrimitive implements ISeriesPrimitive<Time> {
  private bands: PivotBand[] = []
  private series: ISeriesApi<'Candlestick'> | null = null
  private chart: IChartApi | null = null
  private requestUpdate: (() => void) | null = null
  private readonly view: IPrimitivePaneView

  constructor(private readonly chartRef: () => IChartApi | null) {
    this.view = {
      zOrder: () => 'bottom',
      renderer: () => new PivotBandRenderer(this.bands, () => this.series, () => this.chart),
    }
  }

  attached(param: SeriesAttachedParameter<Time>): void {
    this.series = param.series as ISeriesApi<'Candlestick'>
    this.chart = this.chartRef()
    // 保存重绘入口：数据变化时主动请求重绘，否则要等下一次行情推送才刷新
    this.requestUpdate = param.requestUpdate
    this.requestUpdate()
  }

  paneViews(): readonly IPrimitivePaneView[] {
    return [this.view]
  }

  setBands(bands: PivotBand[]): void {
    this.bands = bands
    this.requestUpdate?.()
  }
}

let chart: IChartApi | null = null
let candleSeries: ISeriesApi<'Candlestick'> | null = null
let ema20Series: ISeriesApi<'Line'> | null = null
let ema50Series: ISeriesApi<'Line'> | null = null
let ema200Series: ISeriesApi<'Line'> | null = null
let bollUpperSeries: ISeriesApi<'Line'> | null = null
let bollMiddleSeries: ISeriesApi<'Line'> | null = null
let bollLowerSeries: ISeriesApi<'Line'> | null = null
// 信号用快慢线（1h 提速参数 10/30 时后端下发，虚线展示，与显示用 EMA20/50 区分）
let emaSigFastSeries: ISeriesApi<'Line'> | null = null
let emaSigSlowSeries: ISeriesApi<'Line'> | null = null
// 线段图层（线段模式下为主线，笔降为细线）
let chanSegmentSeries: ISeriesApi<'Line'> | null = null
// 缠论图层：笔折线 + 中枢上下沿
let chanStrokeSeries: ISeriesApi<'Line'> | null = null
let chanZgSeries: ISeriesApi<'Line'> | null = null
let chanZdSeries: ISeriesApi<'Line'> | null = null
let rsiSeries: ISeriesApi<'Line'> | null = null
let macdHistSeries: ISeriesApi<'Histogram'> | null = null
let difSeries: ISeriesApi<'Line'> | null = null
let deaSeries: ISeriesApi<'Line'> | null = null
let markersPlugin: ISeriesMarkersPluginApi<Time> | null = null
let pivotBands: PivotBandPrimitive | null = null
let priceLines: IPriceLine[] = []
// 最新中枢的上下沿价格线（ZG / ZD 轴标签）
let pivotPriceLines: IPriceLine[] = []
let fitted = false
/** 当前图表最后一根K线的时间（秒），用于识别乱序/断层数据 */
let lastBarTime = 0

function lineOptions(color: string) {
  return {
    color,
    lineWidth: 1 as const,
    priceLineVisible: false,
    lastValueVisible: false,
    crosshairMarkerVisible: false,
  }
}

/**
 * 指标序列 → 折线数据。
 * 序列来自 /api/analysis，K线来自 /api/klines（两次独立请求），
 * 若其间有新K线开出，两者的窗口会错开一根：因此从尾部对齐，而不是按起始下标对齐。
 */
function toLineData(values: (number | null)[] | undefined): { time: UTCTimestamp; value: number }[] {
  if (!values) return []
  const candles = props.candles
  const offset = candles.length - values.length
  const out: { time: UTCTimestamp; value: number }[] = []
  for (let i = 0; i < candles.length; i++) {
    const vi = i - offset
    if (vi < 0 || vi >= values.length) continue
    const v = values[vi]
    if (v != null && isFinite(v)) out.push({ time: candles[i].time as UTCTimestamp, value: v })
  }
  return out
}

function applyPriceFormat() {
  const last = props.candles[props.candles.length - 1]
  if (!last || !candleSeries) return
  const p = pricePrecision(last.close)
  const fmt = { type: 'price' as const, precision: p, minMove: 1 / 10 ** p }
  candleSeries.applyOptions({ priceFormat: fmt })
  for (const s of [ema20Series, ema50Series, ema200Series, emaSigFastSeries, emaSigSlowSeries, bollUpperSeries, bollMiddleSeries, bollLowerSeries])
    s?.applyOptions({ priceFormat: fmt })
}

function applyOverlayLines() {
  const a = props.analysis
  if (!a) return
  ema20Series?.setData(toLineData(a.series['ema20']))
  ema50Series?.setData(toLineData(a.series['ema50']))
  ema200Series?.setData(toLineData(a.series['ema200']))
  bollUpperSeries?.setData(toLineData(a.series['bollUpper']))
  bollMiddleSeries?.setData(toLineData(a.series['bollMiddle']))
  bollLowerSeries?.setData(toLineData(a.series['bollLower']))
  emaSigFastSeries?.setData(toLineData(a.series['emaSigFast']))
  emaSigSlowSeries?.setData(toLineData(a.series['emaSigSlow']))
  chanStrokeSeries?.setData(toLineData(a.series['chanStroke']))
  chanSegmentSeries?.setData(toLineData(a.series['chanSegment']))
  // 中枢改用色带表达（原 chanZg/chanZd 折线保留为空数据，避免视觉重复）
  chanZgSeries?.setData([])
  chanZdSeries?.setData([])
  const pivots = a.chan?.pivots ?? []
  const bands: PivotBand[] = []
  for (const level of a.chanLevels ?? []) {
    if (level.role !== 'primary' && !props.toggles.multiLevel) continue   // 多级别关闭时只画本级别
    for (const p of level.pivots) {
      bands.push({ from: p.fromTime, to: p.toTime, zg: p.zg, zd: p.zd, confirmed: p.isConfirmed, level: level.role })
    }
  }
  if (!bands.length) {
    for (const p of pivots) bands.push({ from: p.fromTime, to: p.toTime, zg: p.zg, zd: p.zd, confirmed: p.isConfirmed, level: 'primary' })
  }
  pivotBands?.setBands(bands)

  // 最新中枢的上下沿额外画到价格轴，便于直接读出 ZG / ZD 价位
  for (const line of pivotPriceLines) candleSeries?.removePriceLine(line)
  pivotPriceLines = []
  const latest = pivots[pivots.length - 1]
  if (latest && candleSeries) {
    pivotPriceLines.push(candleSeries.createPriceLine({
      price: latest.zg, color: 'rgba(214,150,235,0.85)', lineWidth: 1,
      lineStyle: LineStyle.Solid, axisLabelVisible: true, title: 'ZG',
    }))
    pivotPriceLines.push(candleSeries.createPriceLine({
      price: latest.zd, color: 'rgba(214,150,235,0.85)', lineWidth: 1,
      lineStyle: LineStyle.Solid, axisLabelVisible: true, title: 'ZD',
    }))
  }
  rsiSeries?.setData(toLineData(a.series['rsi14']))
  macdHistSeries?.setData(
    toLineData(a.macd.hist).map(d => ({
      time: d.time,
      value: d.value,
      color: d.value >= 0 ? 'rgba(38,166,154,0.7)' : 'rgba(239,83,80,0.7)',
    })),
  )
  difSeries?.setData(toLineData(a.macd.dif))
  deaSeries?.setData(toLineData(a.macd.dea))
}

/** 支撑/阻力水平线（绿=支撑、红=阻力；触碰次数多的画实线） */
function applyLevels() {
  if (!candleSeries) return
  for (const line of priceLines) candleSeries.removePriceLine(line)
  priceLines = []
  const a = props.analysis
  if (!a || !props.toggles.levels) return
  let r = 0
  let s = 0
  for (const level of a.levels) {
    const isResistance = level.kind === 'resistance'
    const name = isResistance ? `R${++r}` : `S${++s}`
    priceLines.push(
      candleSeries.createPriceLine({
        price: level.price,
        color: isResistance ? 'rgba(239,83,80,0.75)' : 'rgba(38,166,154,0.75)',
        lineWidth: 1,
        lineStyle: level.strength >= 3 ? LineStyle.Solid : LineStyle.Dashed,
        axisLabelVisible: true,
        title: `${name}×${level.strength}`,
      }),
    )
  }
}

/** 买卖点箭头：买点在K线下方 ▲、卖点在上方 ▼；缠论来源用方形标记区分 */
function applyMarkers() {
  if (!markersPlugin) return
  const a = props.analysis
  const markers: SeriesMarker<Time>[] = []
  if (a && props.toggles.signals) {
    for (const sig of a.signals) {
      const isChan = sig.source === '缠论'
      if (isChan) {
        // 缠论买卖点为主：按类别用 1/2/3 标注，颜色区分买卖
        markers.push({
          time: sig.time as UTCTimestamp,
          position: sig.side === 'buy' ? 'belowBar' : 'aboveBar',
          color: sig.side === 'buy' ? '#c678dd' : '#e08fd0',
          shape: 'square',
          size: 2,
          text: sig.note.startsWith('[1') ? '1' : sig.note.startsWith('[2') ? '2' : '3',
        })
      } else {
        // 辅助指标信号：弱化展示
        markers.push({
          time: sig.time as UTCTimestamp,
          position: sig.side === 'buy' ? 'belowBar' : 'aboveBar',
          color: sig.side === 'buy' ? 'rgba(38,166,154,0.45)' : 'rgba(239,83,80,0.45)',
          shape: 'circle',
          size: 0.6,
          text: '',
        })
      }
    }
  }
  markersPlugin.setMarkers(markers)
}

function applyVisibility() {
  ema20Series?.applyOptions({ visible: props.toggles.ema })
  ema50Series?.applyOptions({ visible: props.toggles.ema })
  ema200Series?.applyOptions({ visible: props.toggles.ema })
  bollUpperSeries?.applyOptions({ visible: props.toggles.boll })
  bollMiddleSeries?.applyOptions({ visible: props.toggles.boll })
  bollLowerSeries?.applyOptions({ visible: props.toggles.boll })
  emaSigFastSeries?.applyOptions({ visible: props.toggles.ema })
  emaSigSlowSeries?.applyOptions({ visible: props.toggles.ema })
  // 线段模式下：线段为主线（粗、亮），笔降为细线作参考；笔模式下沿用原样式
  const segMode = props.toggles.segments && !!props.analysis?.chan?.levelMode && props.analysis.chan.levelMode === 'segment'
  chanStrokeSeries?.applyOptions({
    visible: props.toggles.chan,
    lineWidth: segMode ? 1 : 2,
    color: segMode ? 'rgba(232,192,125,0.45)' : '#e8c07d',
  })
  chanSegmentSeries?.applyOptions({ visible: props.toggles.chan && segMode })
  chanZgSeries?.applyOptions({ visible: props.toggles.chan })
  chanZdSeries?.applyOptions({ visible: props.toggles.chan })
  rsiSeries?.applyOptions({ visible: props.toggles.rsi })
  macdHistSeries?.applyOptions({ visible: props.toggles.macd })
  difSeries?.applyOptions({ visible: props.toggles.macd })
  deaSeries?.applyOptions({ visible: props.toggles.macd })
  applyLevels()
  applyMarkers()
}

/** 副图高度：用拉伸因子（主图:RSI:MACD = 6:1:1），对窗口缩放稳定 */
function applyPaneHeights() {
  if (!chart) return
  const panes = chart.panes()
  if (panes.length < 3) return
  panes[0].setStretchFactor(6)
  panes[1].setStretchFactor(1)
  panes[2].setStretchFactor(1)
}

function applySnapshot() {
  if (!chart || !candleSeries) return
  candleSeries.setData(
    props.candles.map(c => ({
      time: c.time as UTCTimestamp,
      open: c.open,
      high: c.high,
      low: c.low,
      close: c.close,
    })),
  )
  lastBarTime = props.candles.length ? props.candles[props.candles.length - 1].time : 0
  applyPriceFormat()
  applyOverlayLines()
  applyLevels()
  applyMarkers()
  applyPaneHeights()
  if (!fitted) {
    chart.timeScale().fitContent()
    fitted = true
  }
}

onMounted(() => {
  if (!container.value) return
  chart = createChart(container.value, {
    autoSize: true,
    layout: {
      background: { type: ColorType.Solid, color: '#0e1013' },
      textColor: '#9aa3b0',
      panes: {
        separatorColor: '#23262e',
        separatorHoverColor: 'rgba(91,141,239,0.3)',
        enableResize: true,
      },
    },
    grid: {
      vertLines: { color: 'rgba(35,38,46,0.6)' },
      horzLines: { color: 'rgba(35,38,46,0.6)' },
    },
    crosshair: { mode: CrosshairMode.Normal },
    timeScale: {
      borderColor: '#23262e',
      timeVisible: true,
      secondsVisible: false,
      tickMarkFormatter: (time: Time) => formatTime(Number(time)),
    },
    localization: { timeFormatter: (time: Time) => formatTime(Number(time)) },
    rightPriceScale: { borderColor: '#23262e' },
  })

  candleSeries = chart.addSeries(CandlestickSeries, {
    upColor: COLORS.up,
    downColor: COLORS.down,
    wickUpColor: COLORS.up,
    wickDownColor: COLORS.down,
    borderVisible: false,
  })

  ema20Series = chart.addSeries(LineSeries, lineOptions(COLORS.ema20))
  ema50Series = chart.addSeries(LineSeries, lineOptions(COLORS.ema50))
  ema200Series = chart.addSeries(LineSeries, lineOptions(COLORS.ema200))
  bollUpperSeries = chart.addSeries(LineSeries, lineOptions(COLORS.boll))
  bollMiddleSeries = chart.addSeries(LineSeries, lineOptions(COLORS.bollMid))
  bollLowerSeries = chart.addSeries(LineSeries, lineOptions(COLORS.boll))

  const sigLine = (color: string) => ({
    ...lineOptions(color),
    lineStyle: LineStyle.Dashed,
  })
  emaSigFastSeries = chart.addSeries(LineSeries, sigLine('rgba(240,185,11,0.55)'))
  emaSigSlowSeries = chart.addSeries(LineSeries, sigLine('rgba(91,141,239,0.55)'))

  // 缠论：笔折线（细实线）+ 中枢上下沿（细线，无价格轴标签以免与支撑阻力线冲突）
  chanStrokeSeries = chart.addSeries(LineSeries, { color: '#e8c07d', lineWidth: 2, priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false })
  // 线段：更粗、更醒目的配色（线段模式下它是主结构）
  chanSegmentSeries = chart.addSeries(LineSeries, { color: '#f0a050', lineWidth: 3, priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false })
  chanZgSeries = chart.addSeries(LineSeries, { color: 'rgba(198,120,221,0.55)', lineWidth: 1, lineStyle: LineStyle.Dotted, priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false })
  chanZdSeries = chart.addSeries(LineSeries, { color: 'rgba(198,120,221,0.55)', lineWidth: 1, lineStyle: LineStyle.Dotted, priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false })

  rsiSeries = chart.addSeries(
    LineSeries,
    { ...lineOptions(COLORS.rsi), crosshairMarkerVisible: true, priceFormat: { type: 'price', precision: 2, minMove: 0.01 } },
    1,
  )
  rsiSeries.createPriceLine({ price: 70, color: 'rgba(239,83,80,0.4)', lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: false, title: '' })
  rsiSeries.createPriceLine({ price: 30, color: 'rgba(38,166,154,0.4)', lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: false, title: '' })

  macdHistSeries = chart.addSeries(
    HistogramSeries,
    { priceFormat: { type: 'price', precision: 2, minMove: 0.01 }, priceLineVisible: false, lastValueVisible: false },
    2,
  )
  difSeries = chart.addSeries(
    LineSeries,
    { ...lineOptions(COLORS.dif), crosshairMarkerVisible: true, priceFormat: { type: 'price', precision: 2, minMove: 0.01 } },
    2,
  )
  deaSeries = chart.addSeries(
    LineSeries,
    { ...lineOptions(COLORS.dea), crosshairMarkerVisible: true, priceFormat: { type: 'price', precision: 2, minMove: 0.01 } },
    2,
  )

  markersPlugin = createSeriesMarkers(candleSeries, [])
  pivotBands = new PivotBandPrimitive(() => chart)
  candleSeries.attachPrimitive(pivotBands)

  applySnapshot()
  applyVisibility()
  // autoSize 布局稳定后再校一次副图比例
  requestAnimationFrame(() => applyPaneHeights())
})

onBeforeUnmount(() => {
  chart?.remove()
  chart = null
})

watch(() => props.candles, applySnapshot)
watch(
  () => props.analysis,
  () => {
    // 分析结果与当前图表标的必须一致（切换瞬间可能先到新分析、后到新K线）
    const a = props.analysis
    if (!a || a.symbol !== props.symbol || a.interval !== props.interval) return
    applyOverlayLines()
    applyLevels()
    applyMarkers()
    // 可见性/线宽取决于 levelMode（线段模式：线段为主、笔降为细线），
    // 因此分析结果更新后必须重跑一次，否则切换线段后图层样式不会跟着变
    applyVisibility()
  },
)
watch(() => props.toggles, applyVisibility, { deep: true })
// 切币种/周期后需要重新自适应视图（新一轮数据到达时 applySnapshot 会执行 fitContent）
watch([() => props.symbol, () => props.interval], () => {
  fitted = false
})

/**
 * WS 增量：更新（或追加）最后一根K线。
 * - 乱序/重复数据（时间早于图表末根）直接忽略，避免 lightweight-charts 报错后整条链路失效；
 * - 出现跨越多根的断层（例如断线重连后跳过多根K线）时通知上层重新拉取快照补齐。
 */
function updateLast(candle: Candle) {
  if (!candleSeries) return
  if (lastBarTime && candle.time < lastBarTime) return
  const barSeconds = intervalSeconds(props.interval)
  if (lastBarTime && candle.time > lastBarTime + barSeconds) {
    emit('stale')
    return
  }
  lastBarTime = candle.time
  candleSeries.update({
    time: candle.time as UTCTimestamp,
    open: candle.open,
    high: candle.high,
    low: candle.low,
    close: candle.close,
  })
}

/** 右栏点击点位/信号：把价格轴视区平移到该价位附近 */
function locatePrice(price: number) {
  if (!candleSeries) return
  try {
    candleSeries.priceScale().setVisibleRange({ from: price * 0.985, to: price * 1.015 })
  } catch {
    // 忽略定位失败
  }
}

defineExpose({ updateLast, locatePrice })
</script>

<template>
  <div ref="container" class="chart-container"></div>
</template>

<style scoped>
.chart-container {
  position: absolute;
  inset: 0;
}
</style>
