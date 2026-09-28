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

let chart: IChartApi | null = null
let candleSeries: ISeriesApi<'Candlestick'> | null = null
let ema20Series: ISeriesApi<'Line'> | null = null
let ema50Series: ISeriesApi<'Line'> | null = null
let ema200Series: ISeriesApi<'Line'> | null = null
let bollUpperSeries: ISeriesApi<'Line'> | null = null
let bollMiddleSeries: ISeriesApi<'Line'> | null = null
let bollLowerSeries: ISeriesApi<'Line'> | null = null
let rsiSeries: ISeriesApi<'Line'> | null = null
let macdHistSeries: ISeriesApi<'Histogram'> | null = null
let difSeries: ISeriesApi<'Line'> | null = null
let deaSeries: ISeriesApi<'Line'> | null = null
let markersPlugin: ISeriesMarkersPluginApi<Time> | null = null
let priceLines: IPriceLine[] = []
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
  for (const s of [ema20Series, ema50Series, ema200Series, bollUpperSeries, bollMiddleSeries, bollLowerSeries])
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

/** 买卖点箭头：买点在K线下方 ▲、卖点在上方 ▼ */
function applyMarkers() {
  if (!markersPlugin) return
  const a = props.analysis
  const markers: SeriesMarker<Time>[] = []
  if (a && props.toggles.signals) {
    for (const sig of a.signals) {
      markers.push({
        time: sig.time as UTCTimestamp,
        position: sig.side === 'buy' ? 'belowBar' : 'aboveBar',
        color: sig.side === 'buy' ? COLORS.up : COLORS.down,
        shape: sig.side === 'buy' ? 'arrowUp' : 'arrowDown',
        text: sig.side === 'buy' ? '买' : '卖',
      })
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
