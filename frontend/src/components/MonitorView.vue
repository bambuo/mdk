<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Message } from '@arco-design/web-vue'
import {
  addWatchlist, fetchSymbols, fetchWatchlist, fetchWatchlistSignals, removeWatchlist, toggleWatchlist,
} from '../api/client'
import type { MarketKind, SymbolQuote, WatchlistItemView, WatchlistSignalView } from '../types'
import { formatPrice, formatTime } from '../utils'

const emit = defineEmits<{
  (e: 'open', payload: { market: MarketKind; symbol: string }): void
}>()

const market = ref<MarketKind>('spot')
const symbols = ref<SymbolQuote[]>([])
const symbol = ref<string | undefined>(undefined)
const intervals = ref<string[]>(['1h'])
const items = ref<WatchlistItemView[]>([])
const signals = ref<WatchlistSignalView[]>([])
const loading = ref(false)
const adding = ref(false)
let timer: ReturnType<typeof setInterval> | null = null

const marketOptions = [
  { value: 'spot', label: '现货' },
  { value: 'futures', label: '合约' },
]
const intervalOptions = ['5m', '15m', '30m', '1h', '2h', '4h', '6h', '12h', '1d', '3d', '1w']
  .map(v => ({ label: v, value: v }))

const symbolOptions = computed(() =>
  symbols.value.filter(s => !s.pegged).map(s => ({ label: `${s.baseAsset}/${s.quoteAsset}`, value: s.symbol })),
)

/**
 * 证据强度徽章（仅缠论买卖点有）：同语境（类别 × 周期）台账经验表现；
 * 口径与查看页一致——不给未校准的"置信度"，波次不足时明说"样本不足"。
 */
function credText(sig: WatchlistSignalView): string {
  const b = sig.credibility
  if (!b) return sig.source === '缠论' ? '无历史样本' : '—'
  if (!b.sufficient) return `样本不足 · ${b.nEpisodes}波`
  const half = Math.round((b.winRateHigh - b.winRateLow) * 50)
  return `证据 ${Math.round(b.winRate * 100)}%±${half} · ${b.nEpisodes}波`
}

function credClass(sig: WatchlistSignalView): string {
  const b = sig.credibility
  if (!b || !b.sufficient) return 'unknown'
  return b.netPositiveRate >= 0.5 ? 'good' : b.netPositiveRate >= 0.42 ? 'mid' : 'weak'
}

function rowTitle(sig: WatchlistSignalView): string {
  const parts = [sig.note ?? '', `共振：${confluenceLabel(sig.confluence)}`]
  if (!sig.isConfirmed) parts.push('盘中预警（未确认）')
  return parts.filter(Boolean).join('\n')
}

function credTitle(sig: WatchlistSignalView): string {
  const b = sig.credibility
  if (!b) return sig.source === '缠论' ? '该语境暂无已评估样本' : '证据强度仅对缠论买卖点定义，指标状态不评级'
  const pct = (v: number) => `${Math.round(v * 100)}%`
  return [
    `同语境历史表现（缠论 ${sig.kind ?? ''} · ${sig.interval}）`,
    `独立波次 ${b.nEpisodes}（原始 ${b.n} 条）`,
    `胜率 ${pct(b.winRate)}（95% 区间 ${pct(b.winRateLow)}–${pct(b.winRateHigh)}）`,
    `扣费后为正 ${pct(b.netPositiveRate)} · 中位超额 ${(b.medianExcess * 100).toFixed(2)}%`,
    `典型止损距离 ${(b.medianRiskPct * 100).toFixed(2)}%`,
    '口径：波次去重 · 扣费后 · 仅供判断证据强度，不构成交易建议',
  ].join('\n')
}

async function loadSymbols() {
  try {
    symbols.value = await fetchSymbols(market.value)
  } catch (err) {
    Message.warning(err instanceof Error ? `交易对列表加载失败：${err.message}` : '交易对列表加载失败')
  }
}

async function refresh() {
  loading.value = true
  try {
    const [list, stream] = await Promise.all([fetchWatchlist(), fetchWatchlistSignals(50)])
    items.value = list
    signals.value = stream
  } catch {
    // 轮询失败静默：保留上一次结果，避免整页闪空
  } finally {
    loading.value = false
  }
}

async function add() {
  if (!symbol.value) {
    Message.warning('请先搜索并选择交易对')
    return
  }
  if (!intervals.value.length) {
    Message.warning('请至少选择一个周期')
    return
  }
  adding.value = true
  try {
    await addWatchlist(market.value, symbol.value, intervals.value)
    Message.success('已加入监控，后台开始分析')
    await refresh()
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '添加失败')
  } finally {
    adding.value = false
  }
}

async function remove(item: WatchlistItemView) {
  try {
    await removeWatchlist(item.market, item.symbol)
    await refresh()
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '移除失败')
  }
}

async function toggle(item: WatchlistItemView) {
  try {
    await toggleWatchlist(item.market, item.symbol)
    await refresh()
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '操作失败')
  }
}

function resonanceLabel(r: string): string {
  switch (r) {
    case 'aligned': return '共振·同向'
    case 'mixed': return '多空分歧'
    case 'pending': return '待分析'
    default: return '无共振'
  }
}

function confluenceLabel(c: string | null): string {
  if (c === 'aligned') return '共振'
  if (c === 'counter') return '逆向'
  return '—'
}

function strokeLabel(s: { strokeDirection: string; strokeConfirmed: boolean }): string {
  if (s.strokeDirection === 'up') return s.strokeConfirmed ? '向上笔·已确认' : '向上笔·未确认'
  if (s.strokeDirection === 'down') return s.strokeConfirmed ? '向下笔·已确认' : '向下笔·未确认'
  return '无笔'
}

watch(market, async () => {
  symbol.value = undefined
  await loadSymbols()
})

onMounted(async () => {
  await loadSymbols()
  await refresh()
  timer = setInterval(refresh, 15000)
})

onBeforeUnmount(() => {
  if (timer) clearInterval(timer)
})
</script>

<template>
  <div class="monitor-view">
    <section class="add-bar">
      <span class="bar-title">添加监控</span>
      <a-radio-group v-model="market" type="button" size="small">
        <a-radio v-for="m in marketOptions" :key="m.value" :value="m.value">{{ m.label }}</a-radio>
      </a-radio-group>
      <a-select
        v-model="symbol"
        style="width: 210px"
        :options="symbolOptions"
        allow-search
        allow-clear
        size="small"
        :placeholder="market === 'futures' ? '搜索合约（如 BTC）' : '搜索交易对（如 BTC）'"
      />
      <a-select
        v-model="intervals"
        style="width: 240px"
        :options="intervalOptions"
        multiple
        size="small"
        placeholder="选择周期（可多选）"
      />
      <a-button type="primary" size="small" :loading="adding" @click="add">加入监控</a-button>
      <a-spin v-if="loading" :size="14" />
    </section>

    <div class="monitor-body">
      <section class="pane list-pane">
        <h3 class="pane-title">监控列表 <i>{{ items.length }}</i></h3>
        <div v-for="item in items" :key="`${item.market}|${item.symbol}`" class="watch-item" :class="{ off: !item.enabled }">
          <div class="wi-head">
            <a class="pair" @click="emit('open', { market: item.market, symbol: item.symbol })">
              {{ item.baseAsset }}/{{ item.quoteAsset }}
            </a>
            <span class="mkt">{{ item.market === 'futures' ? '合约' : '现货' }}</span>
            <span class="res" :class="item.resonance">{{ resonanceLabel(item.resonance) }}</span>
            <span class="spacer" />
            <a-button size="mini" @click="toggle(item)">{{ item.enabled ? '停用' : '启用' }}</a-button>
            <a-button size="mini" status="danger" @click="remove(item)">移除</a-button>
          </div>
          <div v-for="s in item.snapshots" :key="s.interval" class="snap">
            <span class="iv">{{ s.interval }}</span>
            <span class="stroke" :class="s.strokeDirection">{{ strokeLabel(s) }}</span>
            <span class="pivot">
              {{ s.pivotZg != null && s.pivotZd != null ? `${formatPrice(s.pivotZd)} ~ ${formatPrice(s.pivotZg)}` : '无中枢' }}
            </span>
            <span class="sig" :class="s.lastSide ?? ''">
              <template v-if="s.lastKind">{{ s.lastKind }}<i v-if="s.barsSinceSignal != null"> · {{ s.barsSinceSignal }} 根前</i></template>
              <template v-else>无买卖点</template>
            </span>
            <div v-if="s.indicatorStates.length" class="snap-ind">
              <span v-for="ind in s.indicatorStates" :key="ind.source" class="ind" :class="ind.tone">
                {{ ind.source }}·{{ ind.state }}
              </span>
            </div>
          </div>
          <p v-if="!item.snapshots.length" class="waiting">等待后台分析…</p>
        </div>
        <p v-if="!items.length" class="waiting">还没有监控交易对：在上方搜索交易对、选择周期后点「加入监控」。</p>
      </section>

      <section class="pane stream-pane">
        <h3 class="pane-title">信号流（监控列表内 · 已确认） <i>{{ signals.length }}</i></h3>
        <div class="stream-row hd">
          <span>时间</span><span>标的</span><span>周期</span><span>类型</span><span>方向</span><span>证据强度</span>
        </div>
        <div
          v-for="s in signals"
          :key="`${s.symbol}|${s.interval}|${s.time}|${s.side}|${s.source}`"
          class="stream-row"
          :title="rowTitle(s)"
        >
          <span class="t">{{ formatTime(s.time) }}</span>
          <span class="sym">{{ s.baseAsset }}/{{ s.quoteAsset }}</span>
          <span>{{ s.interval }}</span>
          <span class="kind">{{ s.source }}{{ s.kind ? ` ${s.kind}` : '' }}</span>
          <span :class="s.side">{{ s.side === 'buy' ? '买' : '卖' }}</span>
          <span class="cred-badge" :class="credClass(s)" :title="credTitle(s)">{{ credText(s) }}</span>
        </div>
        <p v-if="!signals.length" class="waiting">监控列表内暂无已确认信号。</p>
      </section>
    </div>
  </div>
</template>

<style scoped>
.monitor-view {
  flex: 1 1 auto;
  min-height: 0;
  display: flex;
  flex-direction: column;
  background: #0e1013;
}

.add-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 14px;
  border-bottom: 1px solid #23262e;
  background: #111418;
  flex: 0 0 auto;
}

.bar-title {
  color: #8b93a1;
  margin-right: 4px;
}

.monitor-body {
  flex: 1 1 auto;
  min-height: 0;
  display: flex;
}

.pane {
  min-height: 0;
  overflow-y: auto;
  padding: 10px 14px;
}

.list-pane {
  flex: 1 1 60%;
  border-right: 1px solid #23262e;
}

.stream-pane {
  flex: 1 1 40%;
}

.pane-title {
  margin: 0 0 10px;
  font-size: 13px;
  font-weight: 600;
  color: #d1d4dc;
}

.pane-title i {
  font-style: normal;
  color: #6b7280;
  font-weight: 400;
}

.watch-item {
  border: 1px solid #23262e;
  border-radius: 6px;
  padding: 8px 10px;
  margin-bottom: 10px;
  background: #14171c;
}

.watch-item.off {
  opacity: 0.5;
}

.wi-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.wi-head .pair {
  color: #e8eaed;
  font-weight: 600;
  cursor: pointer;
}

.wi-head .pair:hover {
  color: #4c9aff;
}

.mkt {
  color: #8b93a1;
}

.spacer {
  flex: 1 1 auto;
}

.res {
  font-size: 12px;
  padding: 1px 6px;
  border-radius: 4px;
  background: #23262e;
  color: #8b93a1;
}

.res.aligned {
  color: #26a69a;
}

.res.mixed {
  color: #f0b90b;
}

.snap {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 6px;
  font-size: 12px;
  color: #9aa2ad;
}

.snap .iv {
  width: 34px;
  color: #d1d4dc;
  font-weight: 600;
}

.snap .stroke.up {
  color: #26a69a;
}

.snap .stroke.down {
  color: #ef5350;
}

.snap .sig.buy {
  color: #26a69a;
}

.snap .sig.sell {
  color: #ef5350;
}

.snap .sig i {
  font-style: normal;
  color: #6b7280;
}

.waiting {
  color: #6b7280;
  font-size: 12px;
  padding: 6px 0;
}

.snap-ind {
  display: flex;
  flex-wrap: wrap;
  gap: 2px 10px;
  padding: 1px 2px 3px;
  font-size: 10px;
  color: #6b7280;
}

.snap-ind .ind.bull {
  color: #26a69a;
}

.snap-ind .ind.bear {
  color: #ef5350;
}

.snap-ind i {
  font-style: normal;
  color: #4a5058;
}

.cred-badge {
  display: inline-block;
  padding: 0 6px;
  border-radius: 3px;
  border: 1px solid var(--color-border-2, #2a2f38);
  font-size: 11px;
  line-height: 16px;
  white-space: nowrap;
  color: #9aa3b0;
}

.cred-badge.good {
  border-color: rgba(38, 166, 154, 0.45);
  color: #26a69a;
}

.cred-badge.mid {
  border-color: rgba(240, 185, 11, 0.4);
  color: #f0b90b;
}

.cred-badge.weak {
  border-color: rgba(239, 83, 80, 0.4);
  color: #ef5350;
}

.cred-badge.unknown {
  border-style: dashed;
  color: #6b7280;
}

.kind {
  color: #9aa3b0;
}

.stream-row {
  display: grid;
  grid-template-columns: 60px 1fr 40px 74px 34px 104px;
  gap: 6px;
  padding: 4px 2px;
  border-bottom: 1px solid #1b1e24;
  font-size: 12px;
  color: #c3c8d0;
}

.stream-row.hd {
  color: #6b7280;
  border-bottom-color: #23262e;
}

.stream-row .t {
  color: #8b93a1;
}

.stream-row .sym {
  color: #e8eaed;
}

.stream-row .buy {
  color: #26a69a;
}

.stream-row .sell {
  color: #ef5350;
}
</style>
