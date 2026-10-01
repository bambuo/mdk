<script setup lang="ts">
import { Message } from '@arco-design/web-vue'
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { fetchAnalysis, fetchKlines, fetchSignalStats, fetchSymbols, openWatchlistSignalStream } from './api/client'
import { useKlineSocket } from './composables/useKlineSocket'
import { useSignalNotify } from './composables/useSignalNotify'
import AnalysisPanel from './components/AnalysisPanel.vue'
import ChartPanel from './components/ChartPanel.vue'
import MonitorView from './components/MonitorView.vue'
import TopToolbar from './components/TopToolbar.vue'
import type { AnalysisResult, Candle, MarketKind, SignalStatsResponse, SymbolQuote, Toggles } from './types'

/** 页面视图：查看（单标的图表）/ 监控（持久化监控列表） */
const view = ref<'chart' | 'monitor'>('chart')

const symbols = ref<SymbolQuote[]>([])
const market = ref<MarketKind>('spot')
const symbol = ref('BTCUSDT')
const interval = ref('1h')
// 用 ref 而非 reactive：TopToolbar 通过 v-model:toggles 整体替换对象
// 纯缠论：缠论图层默认开；EMA/RSI/MACD 仅作图表指标（不再产生信号）；BOLL 与支撑阻力线默认关
const toggles = ref<Toggles>({
  chan: true,
  pivots: true,
  multiLevel: true,
  ema: true,
  rsi: true,
  macd: true,
  boll: false,
  levels: false,
  signals: true,
})

const candles = ref<Candle[]>([])
const analysis = ref<AnalysisResult | null>(null)
// WS 实时价：驱动顶栏价格徽标（K线增量或分析推送都会更新）
const livePrice = ref<number | null>(null)
// 最近一次收到实时推送的时间（用于展示"实时"新鲜度）
const lastPushAt = ref<number | null>(null)
// 信号历史绩效（按当前 市场/币种/周期 查询）
const signalStats = ref<SignalStatsResponse | null>(null)
// 信号桌面提醒：抽到 composables/useSignalNotify（图表路径与监控列表路径共用去重与冷却）
const {
  notifyEnabled, setEnabled: setNotifyEnabled, primeSeen, notify,
} = useSignalNotify()

function toggleNotify() {
  if (notifyEnabled.value) {
    setNotifyEnabled(false)
    return
  }
  if (!('Notification' in window)) {
    Message.warning('当前浏览器不支持桌面通知')
    return
  }
  Notification.requestPermission().then(permission => {
    if (permission === 'granted') {
      setNotifyEnabled(true)
      Message.success('已开启信号提醒：新的已确认缠论买卖点会弹桌面通知（含监控列表，同标的 30 分钟冷却）')
    } else {
      Message.warning('通知权限被拒绝，请在浏览器设置中允许')
    }
  })
}

/** 图表路径：对本次分析结果里的新信号发通知（WS 推送时调用） */
function notifyNewChanSignals(result: AnalysisResult) {
  notify(result.signals
    .filter(s => s.source === '缠论' && s.isConfirmed)
    .map(s => ({
      symbol: result.symbol, baseAsset: result.baseAsset, quoteAsset: result.quoteAsset,
      interval: result.interval, side: s.side, time: s.time, note: s.note,
      price: s.price, stopPrice: s.stopPrice,
    })))
}

/**
 * 监控列表路径（SSE）：无论当前在看哪个图表，监控列表内**新入库**的已确认信号由服务端实时推送。
 * 无历史回放（事件只在信号入库时产生），因此无需"首次标记"；开关关闭时关闭连接。
 */
let closeWatchlistStream: (() => void) | null = null

function syncWatchlistStream() {
  if (notifyEnabled.value && !closeWatchlistStream) {
    closeWatchlistStream = openWatchlistSignalStream(s => notify([{
      symbol: s.symbol, baseAsset: s.baseAsset, quoteAsset: s.quoteAsset,
      interval: s.interval, side: s.side, time: s.time, note: s.note,
      price: s.price, stopPrice: s.stopPrice,
    }]))
  } else if (!notifyEnabled.value && closeWatchlistStream) {
    closeWatchlistStream()
    closeWatchlistStream = null
  }
}

watch(notifyEnabled, syncWatchlistStream)
onMounted(syncWatchlistStream)
onBeforeUnmount(() => closeWatchlistStream?.())

const loading = ref(false)
const errorMsg = ref('')
const chartRef = ref<InstanceType<typeof ChartPanel> | null>(null)

/**
 * 默认选中：跳过锚定币（USDC/USDT 等价格恒定，图表看起来"不动"），优先 BTC。
 * 榜单按成交额排序，锚定币常年居首，直接取第一条会得到一条几乎静止的价格线。
 */
function pickDefaultSymbol(list: SymbolQuote[]): string {
  const tradable = list.filter(s => !s.pegged)
  const pool = tradable.length > 0 ? tradable : list
  const btc = pool.find(s => s.baseAsset === 'BTC')
  return btc?.symbol ?? pool[0]?.symbol ?? 'BTCUSDT'
}

async function loadSymbols() {
  const requestedMarket = market.value
  try {
    const list = await fetchSymbols(requestedMarket)
    if (market.value !== requestedMarket) return // 市场已切换，丢弃过期结果
    symbols.value = list
    if (!list.some(s => s.symbol === symbol.value)) {
      symbol.value = pickDefaultSymbol(list)
    }
  } catch (err) {
    if (market.value !== requestedMarket) return
    Message.warning(err instanceof Error ? `交易对列表加载失败：${err.message}` : '交易对列表加载失败')
  }
}

// 请求序号：快速切换（含线段开关）时，只接受"最后一次请求"的结果，避免先发后到覆盖新状态
let reloadSeq = 0

async function reload() {
  const seq = ++reloadSeq
  loading.value = true
  errorMsg.value = ''
  const requestedMarket = market.value
  const requestedSymbol = symbol.value
  const requestedInterval = interval.value
  try {
    const [klines, result] = await Promise.all([
      fetchKlines(requestedMarket, requestedSymbol, requestedInterval),
      fetchAnalysis(requestedMarket, requestedSymbol, requestedInterval, 500),
    ])
    // 忽略过期响应（用户已切换市场/币种/周期）
    if (market.value !== requestedMarket || symbol.value !== requestedSymbol || interval.value !== requestedInterval) return
    candles.value = klines.candles
    analysis.value = result
    livePrice.value = result.lastPrice
  } catch (err) {
    if (seq !== reloadSeq) return
    errorMsg.value = err instanceof Error ? err.message : String(err)
    candles.value = []
    analysis.value = null
    livePrice.value = null
  } finally {
    if (seq === reloadSeq) loading.value = false
  }
}

async function loadSignalStats() {
  const requestedMarket = market.value
  const requestedSymbol = symbol.value
  const requestedInterval = interval.value
  try {
    const stats = await fetchSignalStats(requestedMarket, requestedSymbol, requestedInterval)
    if (market.value !== requestedMarket || symbol.value !== requestedSymbol || interval.value !== requestedInterval) return
    signalStats.value = stats
  } catch {
    // 统计接口失败不影响主功能
  }
}

const { status } = useKlineSocket(market, symbol, interval, {
  onKline: candle => {
    // 先更新价格徽标：即使图表更新异常，观感上的实时性也不受影响
    livePrice.value = candle.close
    lastPushAt.value = Date.now()
    // 加载（切换币种/周期）期间跳过图表增量：该窗口的K线由随后到达的REST快照覆盖，
    // 直接写入会把新标的的数据画到旧图上，也会误触发断层重载
    if (!loading.value) chartRef.value?.updateLast(candle)
  },
  onAnalysis: result => {
    analysis.value = result
    livePrice.value = result.lastPrice
    notifyNewChanSignals(result)
  },
})

// 首次加载时把已有信号标记为"已见"，避免刚打开页面就喷一堆通知
watch(analysis, first => {
  if (!first) return
  primeSeen(first.signals
    .filter(x => x.source === '缠论' && x.isConfirmed)
    .map(x => ({
      symbol: first.symbol, baseAsset: first.baseAsset, quoteAsset: first.quoteAsset,
      interval: first.interval, side: x.side, time: x.time, note: x.note,
      price: x.price, stopPrice: x.stopPrice,
    })))
})

function onLocate(price: number) {
  chartRef.value?.locatePrice(price)
}

/** 监控页点条目 → 回到查看页并切到该标的 */
function onOpenFromMonitor(payload: { market: MarketKind; symbol: string }) {
  market.value = payload.market
  symbol.value = payload.symbol
  view.value = 'chart'
}

/** 图表发现数据断层（如断线期间跳过多根K线）时，重新拉取快照补齐 */
function onStale() {
  reload()
}

onMounted(async () => {
  await loadSymbols()
  await reload()
  await loadSignalStats()
})

// 仅市场变化时重新拉交易对目录（切周期/切币种不必重拉）
watch(market, async () => {
  const previous = symbol.value
  await loadSymbols()
  if (symbol.value === previous) await reload()
  await loadSignalStats()
  // 若目录校验后改了 symbol，由下面的 [symbol, interval] 监听触发加载
})

watch([symbol, interval], () => {
  reload()
  loadSignalStats()
})

</script>

<template>
  <div class="app">
    <header class="header">
      <TopToolbar
        v-model:market="market"
        v-model:symbol="symbol"
        v-model:interval="interval"
        v-model:toggles="toggles"
        :symbols="symbols"
        :socket-status="status"
        :loading="loading"
        :live-price="livePrice"
        :last-push-at="lastPushAt"
        :notify-enabled="notifyEnabled"
        @toggle-notify="toggleNotify"
      />
    </header>
    <nav class="view-tabs">
      <button type="button" :class="{ on: view === 'chart' }" @click="view = 'chart'">查看</button>
      <button type="button" :class="{ on: view === 'monitor' }" @click="view = 'monitor'">监控</button>
    </nav>
    <div class="body" v-show="view === 'chart'">
      <main class="chart-area">
        <!-- 图表常驻：切换币种/周期时只换数据，不卸载重建（否则加载期间的实时增量会丢失、缩放被重置） -->
        <ChartPanel
          ref="chartRef"
          :candles="candles"
          :analysis="analysis"
          :toggles="toggles"
          :symbol="symbol"
          :interval="interval"
          @stale="onStale"
        />
        <div v-if="loading" class="chart-mask">
          <a-spin :size="26" tip="加载行情与分析数据…" />
        </div>
        <div v-if="!loading && errorMsg" class="chart-center-tip">
          <a-result status="error" :title="errorMsg" subtitle="请检查网络或稍后重试">
            <template #extra>
              <a-button type="primary" @click="reload">重试</a-button>
            </template>
          </a-result>
        </div>
      </main>
      <aside class="panel">
        <AnalysisPanel :analysis="analysis" :stats="signalStats" @locate="onLocate" />
      </aside>
    </div>
    <MonitorView v-if="view === 'monitor'" @open="onOpenFromMonitor" />
  </div>
</template>
