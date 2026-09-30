<script setup lang="ts">
import { computed, onBeforeUnmount, ref, onMounted } from 'vue'
import type { MarketKind, SymbolQuote, Toggles } from '../types'
import { formatPrice, formatVolume } from '../utils'

const props = defineProps<{
  market: MarketKind
  symbols: SymbolQuote[]
  symbol: string
  interval: string
  toggles: Toggles
  socketStatus: 'connecting' | 'open' | 'closed'
  loading: boolean
  /** WS 推送的最新价（优先于快照价显示） */
  livePrice: number | null
  /** 最近一次收到实时推送的时间戳（毫秒） */
  lastPushAt: number | null
  /** 是否已开启桌面提醒 */
  notifyEnabled: boolean
}>()

/** 每秒走一次，用于展示"最后推送 N 秒前" */
const now = ref(Date.now())
let ticker: ReturnType<typeof setInterval> | null = null
onMounted(() => {
  ticker = setInterval(() => {
    now.value = Date.now()
  }, 1000)
})
onBeforeUnmount(() => {
  if (ticker) clearInterval(ticker)
})

const pushAgeText = computed(() => {
  if (props.lastPushAt == null) return '等待推送'
  const age = Math.max(0, Math.round((now.value - props.lastPushAt) / 1000))
  if (age <= 2) return '实时'
  return `${age}s 前`
})

const pushStale = computed(() => props.lastPushAt != null && now.value - props.lastPushAt > 15000)

const emit = defineEmits<{
  (e: 'toggle-notify'): void
  (e: 'update:market', value: MarketKind): void
  (e: 'update:symbol', value: string): void
  (e: 'update:interval', value: string): void
  (e: 'update:toggles', value: Toggles): void
}>()

const marketOptions: { value: MarketKind; label: string }[] = [
  { value: 'spot', label: '现货' },
  { value: 'futures', label: '合约' },
]

const intervals = ['15m', '30m', '1h', '4h', '1d', '1w']

/**
 * 图层开关分两档：主开关（结构、信号）常显，其余收进「更多图层」——
 * 常显项决定页面在讲什么，收起项是分析辅助，避免工具栏一行九个勾选框。
 */
const primaryItems: { key: keyof Toggles; label: string }[] = [
  { key: 'chan', label: '缠论结构' },
  { key: 'signals', label: '缠论买卖点' },
]

const secondaryItems: { key: keyof Toggles; label: string }[] = [
  { key: 'multiLevel', label: '多级别叠加' },
  { key: 'levels', label: '支撑阻力点位' },
  { key: 'ema', label: 'EMA 均线' },
  { key: 'rsi', label: 'RSI' },
  { key: 'macd', label: 'MACD' },
  { key: 'boll', label: 'BOLL' },
]

const toggleItems = [...primaryItems, ...secondaryItems]

const symbolOptions = computed(() =>
  props.symbols.map(s => ({
    label: `${s.baseAsset}/${s.quoteAsset}`,
    value: s.symbol,
    meta: s,
  })),
)

function filterOption(input: string, option: { label?: string }) {
  const label = option.label ?? ''
  return label.toUpperCase().includes(input.trim().toUpperCase())
}

/** 把"勾选中的键"整体写回 toggles：主开关与浮层各自只持有自己那部分，合并后再发出 */
function applyToggles(keys: string[]): void {
  const next = { ...props.toggles }
  for (const it of toggleItems) next[it.key] = keys.includes(it.key as keyof Toggles)
  emit('update:toggles', next)
}

const primaryKeys = computed<string[]>({
  get: () => primaryItems.filter(it => props.toggles[it.key]).map(it => it.key),
  set: (keys) => applyToggles([...keys, ...secondaryKeys.value]),
})

const secondaryKeys = computed<string[]>({
  get: () => secondaryItems.filter(it => props.toggles[it.key]).map(it => it.key),
  set: (keys) => applyToggles([...primaryKeys.value, ...keys]),
})

/** 「更多图层」按钮上显示已启用的辅助图层数量，收起不等于关闭 */
const secondaryCount = computed(() => secondaryKeys.value.length)

const statusMeta = computed(() => {
  switch (props.socketStatus) {
    case 'open':
      return { color: 'success', text: '实时连接' }
    case 'connecting':
      return { color: 'warning', text: '连接中' }
    default:
      return { color: 'danger', text: '已断开' }
  }
})

const currentMeta = computed(() => props.symbols.find(s => s.symbol === props.symbol))

/** 实时优先：WS 最新价 > 快照价 */
const displayPrice = computed(() => props.livePrice ?? currentMeta.value?.lastPrice ?? null)

/** 快照价与实时价不一致时，涨跌幅按实时价对快照价的比例修正（近似） */
const displayChangePct = computed(() => {
  const meta = currentMeta.value
  if (!meta) return null
  if (props.livePrice == null || meta.lastPrice === 0) return meta.priceChangePercent
  const ratio = props.livePrice / meta.lastPrice - 1
  return meta.priceChangePercent + ratio * 100
})
</script>

<template>
  <div class="toolbar">
    <div class="brand">
      MDK<span class="brand-sub">交易分析工作台</span>
    </div>

    <a-radio-group
      type="button"
      size="small"
      :model-value="market"
      @update:model-value="(v: unknown) => emit('update:market', v as MarketKind)"
    >
      <a-radio v-for="mkt in marketOptions" :key="mkt.value" :value="mkt.value">{{ mkt.label }}</a-radio>
    </a-radio-group>

    <a-select
      class="symbol-select"
      style="width: 210px; flex: 0 0 210px"
      :model-value="symbol"
      :options="symbolOptions"
      allow-search
      allow-clear
      :filter-option="filterOption"
      :placeholder="market === 'futures' ? '搜索合约（如 BTC）' : '搜索交易对（如 BTC）'"
      size="small"
      @update:model-value="(v: unknown) => emit('update:symbol', String(v ?? symbol))"
    />

    <a-radio-group
      type="button"
      size="small"
      :model-value="interval"
      @update:model-value="(v: unknown) => emit('update:interval', String(v))"
    >
      <a-radio v-for="itv in intervals" :key="itv" :value="itv">{{ itv.toUpperCase() }}</a-radio>
    </a-radio-group>

    <a-checkbox-group v-model="primaryKeys" class="toggles" size="small">
      <a-checkbox v-for="item in primaryItems" :key="item.key" :value="item.key">{{ item.label }}</a-checkbox>
    </a-checkbox-group>

    <a-popover trigger="click" position="bl" :content-style="{ padding: '10px 12px' }">
      <a-button size="mini" :type="secondaryCount ? 'secondary' : 'text'">
        更多图层{{ secondaryCount ? ` · ${secondaryCount}` : '' }}
      </a-button>
      <template #content>
        <a-checkbox-group v-model="secondaryKeys" direction="vertical" size="small">
          <a-checkbox v-for="item in secondaryItems" :key="item.key" :value="item.key">{{ item.label }}</a-checkbox>
        </a-checkbox-group>
      </template>
    </a-popover>

    <div class="spacer" />

    <span class="market-tag" :class="market">{{ market === 'futures' ? 'U本位永续' : '现货' }}</span>
    <span v-if="displayPrice != null" class="symbol-meta" :class="(displayChangePct ?? 0) >= 0 ? 'up' : 'down'">
      {{ formatPrice(displayPrice) }}
      ({{ (displayChangePct ?? 0) >= 0 ? '+' : '' }}{{ (displayChangePct ?? 0).toFixed(2) }}%)
    </span>
    <span class="vol">24h额 {{ formatVolume(currentMeta?.quoteVolume ?? 0) }}</span>
    <a-tooltip :content="notifyEnabled ? '关闭信号桌面提醒' : '开启信号桌面提醒（新的已确认缠论信号）'">
      <a-button size="mini" :type="notifyEnabled ? 'primary' : 'text'" @click="emit('toggle-notify')">
        {{ notifyEnabled ? '提醒已开' : '提醒' }}
      </a-button>
    </a-tooltip>
    <span class="status" :class="{ 'status-stale': pushStale }">
      <a-badge :status="statusMeta.color as any" :text="`${statusMeta.text} · ${pushAgeText}`" />
    </span>
    <a-spin v-if="loading" :size="16" class="loading-dot" />
  </div>
</template>

<style scoped>
.toolbar {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 12px;
  overflow: hidden;
}

.toolbar > * {
  flex-shrink: 0;
}

.toolbar > .spacer {
  flex: 1 1 auto;
}

.brand {
  font-size: 16px;
  font-weight: 700;
  color: #f0b90b;
  letter-spacing: 1px;
  white-space: nowrap;
  line-height: 52px;
}

.brand-sub {
  margin-left: 8px;
  font-size: 12px;
  font-weight: 400;
  color: #6b7280;
  letter-spacing: 0;
}

.symbol-select {
  width: 210px;
}

.toggles {
  display: flex;
  gap: 2px;
}

.toggles :deep(.arco-checkbox-label) {
  white-space: nowrap;
}

.spacer {
  flex: 1 1 auto;
}

.market-tag {
  font-size: 11px;
  padding: 1px 8px;
  border-radius: 3px;
  white-space: nowrap;
  border: 1px solid #2c313a;
  color: #9aa3b0;
}

.market-tag.futures {
  color: #f0b90b;
  border-color: rgba(240, 185, 11, 0.45);
  background: rgba(240, 185, 11, 0.08);
}

.symbol-meta {
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  white-space: nowrap;
}

.symbol-meta.up {
  color: #26a69a;
}

.symbol-meta.down {
  color: #ef5350;
}

.vol {
  color: #6b7280;
  font-size: 12px;
  white-space: nowrap;
}

.status {
  white-space: nowrap;
}

.status-stale :deep(.arco-badge-text) {
  color: #f0b90b;
}

.loading-dot {
  margin-left: 4px;
}

@media (max-width: 1560px) {
  .vol {
    display: none;
  }
}
</style>
