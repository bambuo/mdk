<script setup lang="ts">
import { computed } from 'vue'
import type { AnalysisResult } from '../types'
import { formatPct, formatPrice, formatTimeFull } from '../utils'

const props = defineProps<{ analysis: AnalysisResult | null }>()

const emit = defineEmits<{
  (e: 'locate', price: number): void
}>()

/** 点位命名：阻力 R1/R2…（由近及远）、支撑 S1/S2…（由近及远），后端已按近远排序 */
const namedLevels = computed(() => {
  const a = props.analysis
  if (!a) return []
  let r = 0
  let s = 0
  return a.levels.map(l => ({
    ...l,
    name: l.kind === 'resistance' ? `R${++r}` : `S${++s}`,
  }))
})

const resistances = computed(() => namedLevels.value.filter(l => l.kind === 'resistance'))
const supports = computed(() => namedLevels.value.filter(l => l.kind === 'support'))

const recentSignals = computed(() =>
  props.analysis ? [...props.analysis.signals].reverse().slice(0, 20) : [],
)

const trendMeta = computed(() => {
  const dir = props.analysis?.trend.direction
  if (dir === 'LONG') return { text: '做多', color: 'green', hint: '趋势向上，偏向寻找买点' }
  if (dir === 'SHORT') return { text: '做空', color: 'red', hint: '趋势向下，偏向寻找卖点' }
  return { text: '观望', color: 'gray', hint: '方向不明或趋势偏弱，谨慎开仓' }
})

function sideColor(side: string) {
  return side === 'buy' ? '#26a69a' : '#ef5350'
}
</script>

<template>
  <template v-if="analysis">
    <!-- 概念一：趋势方向 -->
    <section class="card">
      <h3 class="card-title">趋势方向</h3>
      <div class="trend-head">
        <a-tag :color="trendMeta.color" size="large" class="trend-tag">{{ trendMeta.text }}</a-tag>
        <div class="trend-score">
          <span class="score-num">{{ analysis.trend.score }}</span>
          <span class="score-label">/ 100 多方占比</span>
        </div>
      </div>
      <a-progress
        :percent="analysis.trend.score"
        :show-text="false"
        :color="trendMeta.color === 'gray' ? '#6b7280' : trendMeta.color === 'green' ? '#26a69a' : '#ef5350'"
        size="small"
      />
      <p class="trend-hint">{{ trendMeta.hint }}</p>
      <ul class="reasons">
        <li v-for="reason in analysis.trend.reasons" :key="reason">{{ reason }}</li>
      </ul>
    </section>

    <!-- 概念二：关键价格点位 -->
    <section class="card">
      <h3 class="card-title">
        关键点位
        <span class="card-sub">点击在图上定位</span>
      </h3>
      <div class="level-group">
        <div class="level-caption resistance">阻力</div>
        <div
          v-for="level in resistances"
          :key="level.name"
          class="level-row"
          @click="emit('locate', level.price)"
        >
          <span class="level-name r">{{ level.name }}</span>
          <span class="level-price">{{ formatPrice(level.price) }}</span>
          <span class="level-dist">{{ formatPct(level.distancePct) }}</span>
          <span class="level-strength" :title="`触碰 ${level.strength} 次`">{{ '★'.repeat(Math.min(level.strength, 5)) }}</span>
        </div>
        <div v-if="!resistances.length" class="level-empty">上方暂无明显阻力</div>
      </div>
      <div class="level-group">
        <div class="level-caption support">支撑</div>
        <div
          v-for="level in supports"
          :key="level.name"
          class="level-row"
          @click="emit('locate', level.price)"
        >
          <span class="level-name s">{{ level.name }}</span>
          <span class="level-price">{{ formatPrice(level.price) }}</span>
          <span class="level-dist">{{ formatPct(level.distancePct) }}</span>
          <span class="level-strength" :title="`触碰 ${level.strength} 次`">{{ '★'.repeat(Math.min(level.strength, 5)) }}</span>
        </div>
        <div v-if="!supports.length" class="level-empty">下方暂无明显支撑</div>
      </div>
    </section>

    <!-- 概念三：买卖点 -->
    <section class="card">
      <h3 class="card-title">
        买卖信号
        <span class="card-sub">{{ analysis.signals.length }} 条，最近在前</span>
      </h3>
      <a-timeline v-if="recentSignals.length" class="signals">
        <a-timeline-item
          v-for="sig in recentSignals"
          :key="`${sig.time}-${sig.source}`"
          :dot-color="sideColor(sig.side)"
        >
          <div class="signal-row" @click="emit('locate', sig.price)">
            <div class="signal-head">
              <a-tag :color="sig.side === 'buy' ? 'green' : 'red'" size="small">
                {{ sig.side === 'buy' ? '买点' : '卖点' }}
              </a-tag>
              <a-tag size="small" color="gray">{{ sig.source }}</a-tag>
              <span class="signal-time">{{ formatTimeFull(sig.time) }}</span>
              <span class="signal-price" :class="sig.side">{{ formatPrice(sig.price) }}</span>
            </div>
            <div class="signal-note">{{ sig.note }}</div>
            <div v-if="sig.stopPrice != null" class="signal-stop">
              止损参考 {{ formatPrice(sig.stopPrice) }}（2×ATR）
            </div>
          </div>
        </a-timeline-item>
      </a-timeline>
      <a-empty v-else description="当前窗口内暂无信号" />
    </section>
  </template>
  <a-empty v-else description="暂无分析数据" class="panel-empty" />
</template>

<style scoped>
.card {
  background: #161a20;
  border: 1px solid #23262e;
  border-radius: 8px;
  padding: 10px 12px;
}

.card-title {
  margin: 0 0 8px;
  font-size: 13px;
  font-weight: 600;
  color: #e5e7eb;
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.card-sub {
  font-size: 11px;
  font-weight: 400;
  color: #6b7280;
}

.trend-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}

.trend-tag {
  font-size: 15px;
  font-weight: 700;
  padding: 2px 14px;
}

.trend-score {
  display: flex;
  align-items: baseline;
  gap: 4px;
}

.score-num {
  font-size: 22px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}

.score-label {
  font-size: 11px;
  color: #6b7280;
}

.trend-hint {
  margin: 8px 0 6px;
  font-size: 12px;
  color: #9aa3b0;
}

.reasons {
  margin: 0;
  padding-left: 16px;
  font-size: 12px;
  color: #9aa3b0;
  line-height: 1.7;
}

.level-group {
  margin-bottom: 8px;
}

.level-caption {
  font-size: 11px;
  margin-bottom: 2px;
}

.level-caption.resistance {
  color: #ef5350;
}

.level-caption.support {
  color: #26a69a;
}

.level-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 3px 6px;
  border-radius: 4px;
  cursor: pointer;
  font-variant-numeric: tabular-nums;
}

.level-row:hover {
  background: #1d222a;
}

.level-name {
  width: 24px;
  font-weight: 700;
  font-size: 11px;
}

.level-name.r {
  color: #ef5350;
}

.level-name.s {
  color: #26a69a;
}

.level-price {
  flex: 1 1 auto;
  text-align: right;
}

.level-dist {
  width: 62px;
  text-align: right;
  color: #9aa3b0;
  font-size: 12px;
}

.level-strength {
  width: 56px;
  text-align: right;
  color: #f0b90b;
  font-size: 10px;
  letter-spacing: 1px;
}

.level-empty {
  font-size: 12px;
  color: #6b7280;
  padding: 4px 6px;
}

.signal-row {
  cursor: pointer;
  padding-bottom: 4px;
}

.signal-row:hover .signal-head {
  filter: brightness(1.2);
}

.signal-head {
  display: flex;
  align-items: center;
  gap: 6px;
}

.signal-time {
  color: #6b7280;
  font-size: 11px;
}

.signal-price {
  margin-left: auto;
  font-variant-numeric: tabular-nums;
  font-weight: 600;
}

.signal-price.buy {
  color: #26a69a;
}

.signal-price.sell {
  color: #ef5350;
}

.signal-note {
  font-size: 12px;
  color: #9aa3b0;
  margin-top: 2px;
}

.signal-stop {
  font-size: 11px;
  color: #6b7280;
  margin-top: 2px;
}

.panel-empty {
  margin-top: 40px;
}
</style>
