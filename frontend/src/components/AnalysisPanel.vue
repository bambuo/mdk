<script setup lang="ts">
import { computed, ref } from 'vue'
import type { AnalysisResult, SignalStatsResponse } from '../types'
import { formatPct, formatPrice, formatTime, formatTimeFull } from '../utils'

const props = defineProps<{
  analysis: AnalysisResult | null
  stats: SignalStatsResponse | null
}>()

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

/** 共振筛选：all=全部，aligned=只看顺大势，counter=只看逆大势（当前证据无显著差异，仅作状态标注） */
const alignment = ref<'all' | 'aligned' | 'counter'>('all')
const alignmentOptions = [
  { value: 'all' as const, label: '全部' },
  { value: 'aligned' as const, label: '顺大势' },
  { value: 'counter' as const, label: '逆大势' },
]

/** 统计面板默认收起：主视图只留信号本身 */
const statsOpen = ref(false)

/** 来源筛选：空数组=全部来源（新增来源自动纳入） */
const selectedSources = ref<string[]>([])

const allSources = computed(() => {
  const set = new Set<string>()
  for (const s of props.analysis?.signals ?? []) set.add(s.source)
  return Array.from(set)
})

const sourceOptions = computed(() => allSources.value.map(s => ({ label: s, value: s })))

const recentSignals = computed(() => {
  const a = props.analysis
  if (!a) return []
  let list = [...a.signals]
  if (selectedSources.value.length) {
    list = list.filter(s => selectedSources.value.includes(s.source))
  }
  if (alignment.value === 'aligned') list = list.filter(s => s.trendAligned === true)
  if (alignment.value === 'counter') list = list.filter(s => s.trendAligned === false)
  return list.reverse().slice(0, 20)
})

const alignmentStats = computed(() => props.stats?.byAlignment ?? [])
const regimeStats = computed(() => props.stats?.byRegime ?? [])

const previewCount = computed(() => props.analysis?.signals.filter(s => !s.isConfirmed).length ?? 0)

/** 状态/共振分组的简写标签（"震荡市 ADX<20" → "震荡"） */
function shortLabel(label: string) {
  if (label.startsWith('震荡')) return '震荡'
  if (label.startsWith('趋势')) return '趋势'
  if (label.startsWith('过渡')) return '过渡'
  if (label.startsWith('早期')) return '早期'
  if (label.startsWith('无高周期')) return '无数据'
  return label
}

function regimeClass(adx: number) {
  if (adx >= 25) return 'regime-trend'
  if (adx < 20) return 'regime-range'
  return ''
}

function gradeColor(grade: string) {
  if (grade === '可参考') return '#26a69a'
  if (grade === '仅观察') return '#f0b90b'
  if (grade === '不达标') return '#ef5350'
  return '#6b7280'
}

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

    <!-- 概念三：买卖点（含分级与历史绩效） -->
    <section class="card">
      <h3 class="card-title">
        买卖信号
        <span class="card-sub">{{ analysis.signals.length }} 条 · 最近在前</span>
      </h3>

      <!-- 统计：默认一行摘要，展开为紧凑数据表（专业终端做法：默认视图只留主内容） -->
      <div class="stats">
        <button class="stats-head" type="button" @click="statsOpen = !statsOpen">
          <span class="chev" :class="{ open: statsOpen }">›</span>
          <template v-if="stats && stats.overall">
            <span class="stats-line">
              近90天 · {{ stats.overall.n }}例/{{ stats.overall.nEpisodes }}波 ·
              整体<span :style="{ color: gradeColor(stats.overall.grade) }">{{ stats.overall.grade }}</span>
            </span>
          </template>
          <span v-else class="stats-line muted">绩效数据积累中</span>
          <span class="stats-hint">{{ statsOpen ? '收起' : '统计详情' }}</span>
        </button>

        <div v-if="statsOpen && stats" class="stats-body">
          <div class="tbl">
            <div class="tbl-row hd">
              <span>来源</span><span>例/波</span><span>胜率</span><span>扣费</span><span class="ta-r">分级</span>
            </div>
            <div v-for="s in stats.bySource" :key="s.source" class="tbl-row"
                 :title="`平均超额 ${(s.avgExcess * 100).toFixed(2)}% · 止损命中 ${(s.stopHitRate * 100).toFixed(0)}%`">
              <span class="src">{{ s.source }}</span>
              <span class="num">{{ s.n }}/{{ s.nEpisodes }}</span>
              <span class="num">{{ (s.winRate * 100).toFixed(0) }}%</span>
              <span class="num">{{ (s.netPositiveRate * 100).toFixed(0) }}%</span>
              <span class="grade ta-r" :style="{ color: gradeColor(s.grade) }">{{ s.grade }}</span>
            </div>
          </div>
          <div v-if="regimeStats.length" class="tbl-note">
            <span class="k">按状态</span>
            <span v-for="b in regimeStats" :key="b.label" class="v" :title="`n=${b.n} · 超额 ${(b.avgExcess * 100).toFixed(2)}%`">
              {{ shortLabel(b.label) }} {{ (b.winRate * 100).toFixed(0) }}%<i>({{ b.n }})</i>
            </span>
          </div>
          <div v-if="alignmentStats.length" class="tbl-note">
            <span class="k">按共振</span>
            <span v-for="b in alignmentStats" :key="b.label" class="v" :title="`n=${b.n} · 超额 ${(b.avgExcess * 100).toFixed(2)}%`">
              {{ shortLabel(b.label) }} {{ (b.winRate * 100).toFixed(0) }}%<i>({{ b.n }})</i>
            </span>
            <span class="flag" title="早期小样本中差异明显，样本增至数百后消失（49% vs 50%）：该过滤无显著预测力，仅作状态标注。">差异不显著</span>
          </div>
          <div class="tbl-foot">持有 12 根 · 已扣费（现货往返 0.2%） · 样本不足百例时不可作为结论</div>
        </div>
      </div>

      <!-- 筛选：单行，弱化视觉权重 -->
      <div class="sigs-controls">
        <div class="seg">
          <button
            v-for="opt in alignmentOptions"
            :key="opt.value"
            type="button"
            :class="{ on: alignment === opt.value }"
            @click="alignment = opt.value"
          >{{ opt.label }}</button>
        </div>
        <a-select
          v-model="selectedSources"
          class="src-select"
          size="mini"
          multiple
          :max-tag-count="0"
          :options="sourceOptions"
          placeholder="全部来源"
        />
      </div>

      <a-timeline v-if="recentSignals.length" class="signals">
        <a-timeline-item
          v-for="sig in recentSignals"
          :key="`${sig.time}-${sig.source}-${sig.isConfirmed}`"
          :dot-color="sig.isConfirmed ? sideColor(sig.side) : '#f0b90b'"
        >
          <div class="signal-row" @click="emit('locate', sig.price)">
            <div class="signal-head">
              <span class="side" :class="sig.side">{{ sig.side === 'buy' ? '买' : '卖' }}</span>
              <span class="src-name">{{ sig.source }}</span>
              <span v-if="!sig.isConfirmed" class="preview-badge">盘中</span>
              <span class="signal-time" :title="formatTimeFull(sig.time)">{{ formatTime(sig.time) }}</span>
              <span class="signal-price" :class="sig.side">{{ formatPrice(sig.price) }}</span>
            </div>
            <div class="signal-meta">
              <span :class="sig.isConfirmed ? 'ok' : 'warn'">{{ sig.isConfirmed ? '已确认' : '未确认' }}</span>
              <span v-if="sig.adx != null" :class="regimeClass(sig.adx)">
                {{ sig.adx >= 25 ? '趋势' : sig.adx < 20 ? '震荡' : '过渡' }} ADX{{ sig.adx.toFixed(0) }}
              </span>
              <span v-if="sig.atrPct != null">波动 {{ (sig.atrPct * 100).toFixed(2) }}%</span>
              <span v-if="sig.trendAligned != null">{{ sig.trendAligned ? '顺大势' : '逆大势' }}</span>
            </div>
            <div class="signal-note">{{ sig.note }}</div>
            <div v-if="sig.stopPrice != null" class="signal-stop">
              止损参考 {{ formatPrice(sig.stopPrice) }}（2×ATR）
            </div>
          </div>
        </a-timeline-item>
      </a-timeline>
      <a-empty v-else :description="alignment === 'all' ? '当前窗口内暂无信号' : '当前筛选下没有信号'" />
      <p v-if="previewCount" class="preview-tip">另有 {{ previewCount }} 条盘中预警在最新K线上（未确认）</p>
    </section>
  </template>
  <a-empty v-else description="暂无分析数据" class="panel-empty" />
</template>

<style scoped>
/* ── 统计：默认一行，展开为数据表 ── */
.stats {
  border-top: 1px solid #1e222a;
  border-bottom: 1px solid #1e222a;
  margin: 0 0 8px;
}

.stats-head {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 2px;
  background: none;
  border: none;
  cursor: pointer;
  color: #9aa3b0;
  font-size: 11px;
  font-family: inherit;
  text-align: left;
}

.stats-head:hover .stats-hint {
  color: #d1d4dc;
}

.chev {
  display: inline-block;
  color: #6b7280;
  transition: transform 0.15s ease;
  font-size: 12px;
}

.chev.open {
  transform: rotate(90deg);
}

.stats-line {
  flex: 1 1 auto;
  font-variant-numeric: tabular-nums;
}

.stats-line.muted {
  color: #6b7280;
}

.stats-hint {
  color: #6b7280;
}

.stats-body {
  padding: 2px 0 6px;
}

.tbl {
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.tbl-row {
  display: grid;
  grid-template-columns: 1fr 52px 44px 44px 52px;
  gap: 4px;
  align-items: center;
  padding: 2px 2px;
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.tbl-row.hd {
  color: #5c6470;
  font-size: 10px;
  border-bottom: 1px solid #1e222a;
  padding-bottom: 3px;
}

.tbl-row .src {
  color: #d1d4dc;
}

.tbl-row .num {
  text-align: right;
  color: #9aa3b0;
}

.tbl-row .grade {
  font-size: 10px;
}

.ta-r {
  text-align: right;
}

.tbl-note {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 6px;
  font-size: 11px;
  color: #9aa3b0;
  font-variant-numeric: tabular-nums;
}

.tbl-note .k {
  color: #5c6470;
  font-size: 10px;
  min-width: 32px;
}

.tbl-note .v i {
  font-style: normal;
  color: #5c6470;
  margin-left: 2px;
}

.tbl-note .flag {
  color: #6b7280;
  border-left: 1px solid #2c313a;
  padding-left: 8px;
  cursor: help;
  font-size: 10px;
}

.tbl-foot {
  margin-top: 6px;
  font-size: 10px;
  color: #5c6470;
}

/* ── 筛选：单行、弱化 ── */
.sigs-controls {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 10px;
}

.seg {
  display: inline-flex;
  border: 1px solid #23262e;
  border-radius: 4px;
  overflow: hidden;
  flex: 0 0 auto;
}

.seg button {
  background: none;
  border: none;
  color: #9aa3b0;
  font-size: 11px;
  font-family: inherit;
  padding: 2px 8px;
  cursor: pointer;
  white-space: nowrap;
}

.seg button + button {
  border-left: 1px solid #23262e;
}

.seg button:hover {
  color: #d1d4dc;
}

.seg button.on {
  background: #23262e;
  color: #e5e7eb;
}

.src-select {
  flex: 1 1 auto;
  min-width: 0;
}

.signal-head .side {
  font-size: 11px;
  font-weight: 700;
  width: 14px;
  text-align: center;
  border-radius: 2px;
  line-height: 15px;
}

.signal-head .side.buy {
  color: #26a69a;
  background: rgba(38, 166, 154, 0.12);
}

.signal-head .side.sell {
  color: #ef5350;
  background: rgba(239, 83, 80, 0.12);
}

.signal-head .src-name {
  font-size: 11px;
  color: #9aa3b0;
}

.preview-badge {
  font-size: 10px;
  color: #f0b90b;
  border: 1px solid rgba(240, 185, 11, 0.35);
  border-radius: 2px;
  padding: 0 3px;
  line-height: 14px;
}

.signal-meta {
  display: flex;
  gap: 0;
  font-size: 11px;
  margin-top: 2px;
  color: #6b7280;
}

.signal-meta > span + span::before {
  content: '·';
  margin: 0 5px;
  color: #3a4048;
}

.signal-meta .ok {
  color: #26a69a;
}

.signal-meta .warn {
  color: #f0b90b;
}

.signal-meta .regime-trend {
  color: #9aa3b0;
}

.signal-meta .regime-range {
  color: #8b93a1;
}

.preview-tip {
  margin: 8px 0 0;
  font-size: 11px;
  color: #f0b90b;
}

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
