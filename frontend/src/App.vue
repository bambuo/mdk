<script setup lang="ts">
import { Message } from '@arco-design/web-vue'
import { onMounted, ref, watch } from 'vue'
import { fetchAnalysis, fetchKlines, fetchSymbols } from './api/client'
import { useKlineSocket } from './composables/useKlineSocket'
import AnalysisPanel from './components/AnalysisPanel.vue'
import ChartPanel from './components/ChartPanel.vue'
import TopToolbar from './components/TopToolbar.vue'
import { isPeggedPair } from './utils'
import type { AnalysisResult, Candle, MarketKind, SymbolQuote, Toggles } from './types'

const symbols = ref<SymbolQuote[]>([])
const market = ref<MarketKind>('spot')
const symbol = ref('BTCUSDT')
const interval = ref('1h')
// 用 ref 而非 reactive：TopToolbar 通过 v-model:toggles 整体替换对象
const toggles = ref<Toggles>({
  ema: true,
  rsi: true,
  macd: true,
  boll: true,
  levels: true,
  signals: true,
})

const candles = ref<Candle[]>([])
const analysis = ref<AnalysisResult | null>(null)
// WS 实时价：驱动顶栏价格徽标（K线增量或分析推送都会更新）
const livePrice = ref<number | null>(null)
// 最近一次收到实时推送的时间（用于展示"实时"新鲜度）
const lastPushAt = ref<number | null>(null)
const loading = ref(false)
const errorMsg = ref('')
const chartRef = ref<InstanceType<typeof ChartPanel> | null>(null)

/**
 * 默认选中：跳过锚定币（USDC/USDT 等价格恒定，图表看起来"不动"），优先 BTC。
 * 榜单按成交额排序，锚定币常年居首，直接取第一条会得到一条几乎静止的价格线。
 */
function pickDefaultSymbol(list: SymbolQuote[]): string {
  const tradable = list.filter(s => !isPeggedPair(s.baseAsset))
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

async function reload() {
  loading.value = true
  errorMsg.value = ''
  const requestedMarket = market.value
  const requestedSymbol = symbol.value
  const requestedInterval = interval.value
  try {
    const [klines, result] = await Promise.all([
      fetchKlines(requestedMarket, requestedSymbol, requestedInterval),
      fetchAnalysis(requestedMarket, requestedSymbol, requestedInterval),
    ])
    // 忽略过期响应（用户已切换市场/币种/周期）
    if (market.value !== requestedMarket || symbol.value !== requestedSymbol || interval.value !== requestedInterval) return
    candles.value = klines.candles
    analysis.value = result
    livePrice.value = result.lastPrice
  } catch (err) {
    if (market.value !== requestedMarket || symbol.value !== requestedSymbol || interval.value !== requestedInterval) return
    errorMsg.value = err instanceof Error ? err.message : String(err)
    candles.value = []
    analysis.value = null
    livePrice.value = null
  } finally {
    if (market.value === requestedMarket && symbol.value === requestedSymbol && interval.value === requestedInterval) {
      loading.value = false
    }
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
  },
})

function onLocate(price: number) {
  chartRef.value?.locatePrice(price)
}

/** 图表发现数据断层（如断线期间跳过多根K线）时，重新拉取快照补齐 */
function onStale() {
  reload()
}

onMounted(async () => {
  await loadSymbols()
  await reload()
})

// 仅市场变化时重新拉交易对目录（切周期/切币种不必重拉）
watch(market, async () => {
  const previous = symbol.value
  await loadSymbols()
  if (symbol.value === previous) await reload()
  // 若目录校验后改了 symbol，由下面的 [symbol, interval] 监听触发加载
})

watch([symbol, interval], () => {
  reload()
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
      />
    </header>
    <div class="body">
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
        <AnalysisPanel :analysis="analysis" @locate="onLocate" />
      </aside>
    </div>
  </div>
</template>
