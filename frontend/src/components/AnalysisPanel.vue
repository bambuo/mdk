<script setup lang="ts">
import { computed, ref } from 'vue'
import type { AnalysisResult, CredibilityBucket, SignalStatsResponse, TradeSignal } from '../types'
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

/** 级别共振筛选：all=全部，aligned=只看"级别共振"信号 */
const confluenceOnly = ref(false)

/** 共振分组绩效（来自 byConfluence） */
const confluenceStats = computed(() => props.stats?.byConfluence ?? [])

/** 次级别数据覆盖提示（覆盖不足时说明从何时开始有数据） */
const lowerCoverageHint = computed(() => {
  const levels = props.analysis?.chanLevels
  if (!levels) return ''
  const lower = levels.find(l => l.role === 'lower')
  const primary = levels.find(l => l.role === 'primary')
  if (!lower || !primary || !lower.pivots.length || !primary.pivots.length) return ''
  const primaryStart = Math.min(...primary.pivots.map(p => p.fromTime))
  if (lower.coverageFromTime <= primaryStart) return ''
  return `次级别数据自 ${formatTime(lower.coverageFromTime)} 起（更早区域未覆盖）`
})

/** 中枢的级别归属说明 */
function pivotTooltip(p: { insideHigher?: boolean | null; lowerPivotCount?: number | null }) {
  const parts: string[] = []
  if (p.insideHigher === true) parts.push('位于高周期中枢区域内（大级别震荡中）')
  if (p.insideHigher === false) parts.push('不在高周期中枢区域内（大级别之外）')
  if (p.lowerPivotCount != null) parts.push(`运行期间形成 ${p.lowerPivotCount} 个次级别中枢`)
  return parts.join(' · ')
}

/** 共振标签展示 */
function confluenceLabel(tag: string | null): string {
  if (tag === 'aligned') return '共振'
  if (tag === 'counter') return '逆向'
  return ''
}

/** 统计面板默认收起：主视图只留信号本身 */
const statsOpen = ref(false)

const recentSignals = computed(() => {
  const a = props.analysis
  if (!a) return []
  let list = [...a.signals]
  if (confluenceOnly.value) list = list.filter(s => s.confluence === 'aligned')
  return list.reverse().slice(0, 20)
})

const regimeStats = computed(() => props.stats?.byRegime ?? [])

const previewCount = computed(() => props.analysis?.signals.filter(s => !s.isConfirmed).length ?? 0)

/**
 * 可信度徽章：按 (来源, 类别) 到台账经验表里查同语境的**实际表现**，
 * 不做加权、不给"置信度分数"——只陈述样本量、胜率区间与扣费后为正比例；
 * 波次不足时明确显示"样本不足"（口径见后端 SignalCredibilityRules）。
 */
const credibilityMap = computed(() => {
  const map = new Map<string, CredibilityBucket>()
  for (const bucket of props.analysis?.credibility ?? []) {
    map.set(bucket.kind ?? '', bucket)
  }
  return map
})

function bucketOf(sig: TradeSignal): CredibilityBucket | null {
  return credibilityMap.value.get(sig.kind ?? '') ?? null
}

function credibilityText(sig: TradeSignal): string {
  const bucket = bucketOf(sig)
  if (!bucket) return '无历史样本'
  if (!bucket.sufficient) return `样本不足 · ${bucket.nEpisodes}波`
  const halfWidth = Math.round((bucket.winRateHigh - bucket.winRateLow) * 50)
  return `证据 ${Math.round(bucket.winRate * 100)}%±${halfWidth} · ${bucket.nEpisodes}波`
}

function credibilityClass(sig: TradeSignal): string {
  const bucket = bucketOf(sig)
  if (!bucket || !bucket.sufficient) return 'unknown'
  if (bucket.netPositiveRate >= 0.5) return 'good'
  return bucket.netPositiveRate >= 0.42 ? 'mid' : 'weak'
}

/** 已走 R 数：入场相对结构参考点已完成的幅度 ÷ 该信号的止损距离（1R） */
function lagText(sig: TradeSignal): string | null {
  return sig.lagShare == null ? null : `已走 ${sig.lagShare.toFixed(2)}R`
}

function lagClass(share: number): string {
  return share >= 1 ? 'weak' : share >= 0.75 ? 'mid' : 'good'
}

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

/**
 * 趋势占比按方向呈现：score 是"多方证据占比"（0–100）。
 * 做空时展示"空方占比"= 100 − score——否则"做空 · 多方占比 0"会被误读为"做空强度为 0"。
 */
const trendShare = computed(() => {
  const direction = props.analysis?.trend.direction
  const score = props.analysis?.trend.score ?? 50
  return direction === 'SHORT'
    ? { num: 100 - score, label: '/ 100 空方占比' }
    : { num: score, label: '/ 100 多方占比' }
})

function gradeColor(grade: string) {
  if (grade === '可实盘') return '#f0b90b'   // 唯一允许对上实盘语义的档位
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
    <!-- 缠论结构（分型/笔/中枢 + 最近买卖点） -->
    <section v-if="analysis.chan" class="card">
      <h3 class="card-title">
        缠论结构
        <span class="card-sub">
          {{ analysis.chan.strokeCount }} 笔 · {{ analysis.chan.pivotCount }} 笔中枢
        </span>
      </h3>
      <div class="chan-grid">
        <span class="k">当前笔</span>
        <span class="v">
          {{ analysis.chan.lastStrokeDirection === 'up' ? '向上笔' : analysis.chan.lastStrokeDirection === 'down' ? '向下笔' : '—' }}
          <i>{{ analysis.chan.lastStrokeConfirmed ? '已确认' : '未确认（可能延伸）' }}</i>
        </span>
        <span class="k">最新中枢</span>
        <span class="v">
          <template v-if="analysis.chan.pivotZg != null">
            {{ formatPrice(analysis.chan.pivotZd!) }} ~ {{ formatPrice(analysis.chan.pivotZg!) }}
            <i>{{ analysis.chan.pivotStrokes }} 笔</i>
          </template>
          <template v-else>—</template>
        </span>
        <span class="k">现价位置</span>
        <span class="v">
          {{ analysis.chan.priceInPivot == null ? '—' : analysis.chan.priceInPivot ? '中枢内（震荡）' : '中枢外（离开段）' }}
        </span>
        <span class="k">最近买卖点</span>
        <span class="v">
          <template v-if="analysis.chan.lastKind">
            {{ analysis.chan.lastKind }} @ {{ formatPrice(analysis.chan.lastPrice!) }}
            <i>{{ analysis.chan.lastTime ? formatTime(analysis.chan.lastTime) : '' }}</i>
          </template>
          <template v-else>—</template>
        </span>
      </div>
      <!-- 高周期结构上下文（级别共振的依据） -->
      <div v-if="analysis.chan.higherContext" class="chan-higher">
        <span class="k">高周期 {{ analysis.chan.higherContext.interval }}</span>
        <span class="v">
          {{ analysis.chan.higherContext.lastStrokeDirection === 'up' ? '向上笔' : analysis.chan.higherContext.lastStrokeDirection === 'down' ? '向下笔' : '—' }}
          <i>{{ analysis.chan.higherContext.priceInPivot == null ? '' : analysis.chan.higherContext.priceInPivot ? '· 价格在中枢内' : '· 价格在中枢外' }}</i>
          <i v-if="analysis.chan.higherContext.lastKind">
            · 最近 {{ analysis.chan.higherContext.lastKind }}
            <span :class="analysis.chan.higherContext.lastSide === 'buy' ? 'up' : 'down'">
              {{ analysis.chan.higherContext.lastSide === 'buy' ? '买' : '卖' }}
            </span>
          </i>
          <i>· 该周期信号 {{ analysis.chan.higherContext.signalCount }} 个</i>
        </span>
      </div>

      <!-- 多级别中枢概览：高周期（大区间）/ 本级别 / 次级别（细粒度） -->
      <div v-if="analysis.chanLevels && analysis.chanLevels.length > 1" class="chan-levels">
        <div class="lv-hd"><span>级别</span><span>中枢</span><span class="ta-r">最新区间</span></div>
        <div v-for="lv in analysis.chanLevels" :key="lv.role" class="lv-row">
          <span class="lv-name" :class="lv.role">
            {{ lv.role === 'higher' ? '高周期' : lv.role === 'primary' ? '本级别' : '次级别' }}
            <i>{{ lv.interval }}</i>
          </span>
          <span class="lv-count">{{ lv.pivots.length }}</span>
          <span class="lv-range ta-r">
            <template v-if="lv.pivots.length">
              {{ formatPrice(lv.pivots[lv.pivots.length - 1].zd) }} ~ {{ formatPrice(lv.pivots[lv.pivots.length - 1].zg) }}
            </template>
            <template v-else>—</template>
          </span>
        </div>
        <p class="lv-hint">
          色带：<span class="dot higher" />高周期 <span class="dot primary" />本级别 <span class="dot lower" />次级别
          <template v-if="lowerCoverageHint"> · {{ lowerCoverageHint }}</template>
        </p>
      </div>

      <!-- 中枢列表：缠论结构的核心，最近的在前 -->
      <div v-if="analysis.chan.pivots.length" class="chan-pivots">
        <div class="pivot-hd"><span>中枢区间</span><span>笔数</span><span>状态</span></div>
        <div v-for="p in [...analysis.chan.pivots].reverse().slice(0, 4)" :key="p.fromTime" class="pivot-row"
             :title="pivotTooltip(p)">
          <span class="pivot-range">
            {{ formatPrice(p.zd) }} ~ {{ formatPrice(p.zg) }}
            <i v-if="p.insideHigher" class="nest">4h内</i>
            <i v-if="p.lowerPivotCount" class="nest">+{{ p.lowerPivotCount }}小</i>
          </span>
          <span class="pivot-strokes">{{ p.strokes }}</span>
          <span class="pivot-state" :class="{ live: !p.isConfirmed }">{{ p.isConfirmed ? '已离开' : '进行中' }}</span>
        </div>
      </div>
      <p v-if="analysis.chan.lastNote" class="chan-note">{{ analysis.chan.lastNote }}</p>
      <p class="chan-hint">
        笔中枢：包含处理 → 分型 → 笔 → 中枢 → 背驰 → 1/2/3 类买卖点。只有已确认结构才产生买卖点。
      </p>
    </section>

    <!-- 概念三：买卖点（含分级与历史绩效） -->
    <section class="card">
      <h3 class="card-title">
        买卖信号
        <span class="card-sub">
          显示 {{ recentSignals.length }} / 共 {{ analysis.signals.length }} 条
        </span>
      </h3>

      <!-- 统计：默认一行摘要，展开为紧凑数据表（专业终端做法：默认视图只留主内容） -->
      <div class="stats">
        <button class="stats-head" type="button" @click="statsOpen = !statsOpen">
          <span class="chev" :class="{ open: statsOpen }">›</span>
          <template v-if="stats && stats.overall">
            <span class="stats-line">
              近90天（按记录时间） · {{ stats.overall.n }}例/{{ stats.overall.nEpisodes }}波 ·
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
                 :title="`${s.gradeReason}\n平均超额 ${(s.avgExcess * 100).toFixed(2)}% · 止损命中 ${(s.stopHitRate * 100).toFixed(0)}% · 单标的占比 ${(s.topSymbolShare * 100).toFixed(0)}%`">
              <span class="src" :title="s.gradeReason">{{ s.source }}</span>
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
          <div v-if="confluenceStats.length" class="tbl-note">
            <span class="k">级别共振</span>
            <span v-for="b in confluenceStats" :key="b.label" class="v" :title="`n=${b.n} · 超额 ${(b.avgExcess * 100).toFixed(2)}%`">
              {{ b.label }} {{ (b.winRate * 100).toFixed(0) }}%<i>({{ b.n }})</i>
            </span>
          </div>
          <div v-if="stats.overall" class="tbl-foot">
            分级依据：{{ stats.overall.gradeReason }}
          </div>
          <!-- 实盘晋升进度：让"离可实盘还差多少"可见，而不是只显示一个档位 -->
          <div class="tbl-note gate">
            <span class="k">实盘判据</span>
            <span class="v">
              实时波次 {{ stats.realtimeEpisodes }} / {{ stats.realtimeRequired }}
              <i v-if="stats.realtimeEpisodes < stats.realtimeRequired">
                （窗口共 {{ stats.windowEpisodes }} 波，还差 {{ stats.realtimeRequired - stats.realtimeEpisodes }} 波实时样本）
              </i>
              <i v-else>（样本量已达标，需同时满足 t≥2 · 中位超额>0 · 扣费后为正≥55% · 分半稳定 · 单标的≤40% · MAE≤1R）</i>
            </span>
          </div>
          <div class="tbl-foot">{{ stats.windowBasis }} · 持有 12 根 · 已扣费（现货往返 0.2%） · 门槛：独立波次 ≥30 且 t≥2 且扣费后为正 ≥50% 且中位超额 >0 且单标的 ≤50%</div>
        </div>
      </div>

      <!-- 筛选：单行，弱化视觉权重 -->
      <div class="sigs-controls">
        <div class="seg">
          <button type="button" :class="{ on: !confluenceOnly }" @click="confluenceOnly = false">全部</button>
          <button type="button" :class="{ on: confluenceOnly }" @click="confluenceOnly = true"
                  title="只显示前 1 根高周期K线内出现同向高周期缠论信号的信号（级别共振）">只看共振</button>
        </div>
      </div>

      <a-timeline v-if="recentSignals.length" class="signals">
        <a-timeline-item
          v-for="sig in recentSignals"
          :key="`${sig.time}-${sig.source}-${sig.isConfirmed}`"
          :dot-color="sig.isConfirmed ? sideColor(sig.side) : '#f0b90b'"
        >
          <div class="signal-row" :title="sig.note" @click="emit('locate', sig.price)">
            <div class="signal-head">
              <span class="side" :class="sig.side">{{ sig.side === 'buy' ? '买' : '卖' }}</span>
              <span class="src-name">{{ sig.kind ? `${sig.source} ${sig.kind}` : sig.source }}</span>
              <span class="signal-time" :title="formatTimeFull(sig.time)">{{ formatTime(sig.time) }}</span>
              <span class="signal-price" :class="sig.side">{{ formatPrice(sig.price) }}</span>
            </div>
            <div class="signal-sub">
              <a-tooltip position="left">
                <span class="cred-badge" :class="credibilityClass(sig)">{{ credibilityText(sig) }}</span>
                <template #content>
                  <div class="cred-tip">
                    <div class="cred-tip-title">
                      同语境历史表现（<template v-if="sig.kind">{{ sig.kind }}</template>
                      · {{ analysis.interval }}）
                    </div>
                    <template v-if="bucketOf(sig)">
                      <div>独立波次 {{ bucketOf(sig)!.nEpisodes }}（原始 {{ bucketOf(sig)!.n }} 条）</div>
                      <div>
                        胜率 {{ Math.round(bucketOf(sig)!.winRate * 100) }}%（95% 区间
                        {{ Math.round(bucketOf(sig)!.winRateLow * 100) }}–{{ Math.round(bucketOf(sig)!.winRateHigh * 100) }}%）
                      </div>
                      <div>扣费后为正 {{ Math.round(bucketOf(sig)!.netPositiveRate * 100) }}%</div>
                      <div>中位超额 {{ (bucketOf(sig)!.medianExcess * 100).toFixed(2) }}%</div>
                      <div>典型止损距离 {{ (bucketOf(sig)!.medianRiskPct * 100).toFixed(2) }}%</div>
                    </template>
                    <div v-else>该语境暂无已评估样本</div>
                    <div class="cred-tip-foot">口径：波次去重 · 扣费后 · 仅供判断证据强度，不构成交易建议</div>
                  </div>
                </template>
              </a-tooltip>
              <a-tooltip v-if="sig.jointScore" position="left">
                <span class="score-badge">打分 {{ sig.jointScore.score }}/{{ sig.jointScore.maxScore }}</span>
                <template #content>
                  <div class="cred-tip">
                    <div class="cred-tip-title">
                      联合打分 {{ sig.jointScore.score }}/{{ sig.jointScore.maxScore }}
                      （等权 5 项 · {{ sig.jointScore.kind }} 的{{ sig.jointScore.kind?.includes('3') ? '延续' : '反转' }}语义）
                    </div>
                    <div v-for="f in sig.jointScore.features" :key="f.name">
                      {{ f.hit ? '✓' : '✗' }} {{ f.name }}
                    </div>
                    <div class="cred-tip-foot">打分不是交易授权——"可实盘"只由实盘晋升判据决定（见统计详情）</div>
                  </div>
                </template>
              </a-tooltip>
              <span class="flex-spacer" />
              <span v-if="lagText(sig)" class="lag" :class="lagClass(sig.lagShare!)" title="入场相对结构参考点已完成的幅度 ÷ 该信号的止损距离（1R）">
                {{ lagText(sig) }}
              </span>
            </div>
            <div class="signal-meta">
              <span v-if="!sig.isConfirmed" class="warn">盘中预警（未确认）</span>
              <span v-if="confluenceLabel(sig.confluence)" class="conf-badge" :class="sig.confluence">
                {{ confluenceLabel(sig.confluence) }}
              </span>
              <span v-if="sig.adx != null" :class="regimeClass(sig.adx)">ADX{{ sig.adx.toFixed(0) }}</span>
              <span v-if="sig.stopPrice != null">
                止损 {{ formatPrice(sig.stopPrice) }}<template v-if="sig.riskPct != null">（{{ (sig.riskPct * 100).toFixed(2) }}%）</template>
              </span>
            </div>
          </div>
        </a-timeline-item>
      </a-timeline>
      <a-empty v-else description="当前窗口内暂无信号" />
      <p v-if="previewCount" class="preview-tip">另有 {{ previewCount }} 条盘中预警在最新K线上（未确认）</p>
    </section>
    <!-- 概念一：趋势方向 -->
    <section class="card">
      <h3 class="card-title">趋势方向</h3>
      <div class="trend-head">
        <a-tag :color="trendMeta.color" size="large" class="trend-tag">{{ trendMeta.text }}</a-tag>
        <div class="trend-score">
          <span class="score-num">{{ trendShare.num }}</span>
          <span class="score-label">{{ trendShare.label }}</span>
        </div>
      </div>
      <a-progress
        :percent="trendShare.num / 100"
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

  </template>
  <a-empty v-else description="暂无分析数据" class="panel-empty" />
</template>

<style scoped>
/* ── 缠论结构卡片 ── */
.chan-grid {
  display: grid;
  grid-template-columns: 64px 1fr;
  gap: 4px 8px;
  font-size: 12px;
}

.chan-grid .k {
  color: #6b7280;
  font-size: 11px;
  line-height: 18px;
}

.chan-grid .v {
  color: #d1d4dc;
  font-variant-numeric: tabular-nums;
  line-height: 18px;
}

.chan-grid .v i {
  font-style: normal;
  color: #6b7280;
  font-size: 11px;
  margin-left: 4px;
}

.chan-note {
  margin: 8px 0 0;
  font-size: 11px;
  color: #9aa3b0;
  line-height: 1.6;
}

.chan-hint {
  margin: 6px 0 0;
  font-size: 10px;
  color: #5c6470;
}

/* ── 高周期上下文与共振标签 ── */
.chan-higher {
  display: grid;
  grid-template-columns: 74px 1fr;
  gap: 4px 8px;
  margin-top: 8px;
  padding-top: 6px;
  border-top: 1px solid #1e222a;
  font-size: 11px;
}

.chan-higher .k {
  color: #6b7280;
}

.chan-higher .v {
  color: #9aa3b0;
}

.chan-higher .v i {
  font-style: normal;
  color: #6b7280;
  margin-left: 2px;
}

.chan-higher .up {
  color: #26a69a;
}

.chan-higher .down {
  color: #ef5350;
}

.conf-badge {
  font-size: 10px;
  border-radius: 2px;
  padding: 0 3px;
  line-height: 14px;
}

.conf-badge.aligned {
  color: #26a69a;
  border: 1px solid rgba(38, 166, 154, 0.45);
  background: rgba(38, 166, 154, 0.1);
}

.conf-badge.counter {
  color: #6b7280;
  border: 1px solid #2c313a;
}

/* ── 多级别概览 ── */
.chan-levels {
  margin-top: 8px;
  padding-top: 6px;
  border-top: 1px solid #1e222a;
}

.lv-hd,
.lv-row {
  display: grid;
  grid-template-columns: 1fr 30px 1fr;
  gap: 6px;
  font-size: 11px;
  padding: 1px 0;
}

.lv-hd {
  color: #5c6470;
  font-size: 10px;
}

.lv-name i {
  font-style: normal;
  color: #6b7280;
  margin-left: 3px;
  font-size: 10px;
}

.lv-name.higher {
  color: #f0a050;
}

.lv-name.primary {
  color: #c678dd;
}

.lv-name.lower {
  color: #5aaaf0;
}

.lv-count {
  text-align: right;
  color: #9aa3b0;
  font-variant-numeric: tabular-nums;
}

.lv-range {
  color: #d1d4dc;
  font-variant-numeric: tabular-nums;
}

.lv-hint {
  margin: 4px 0 0;
  font-size: 10px;
  color: #5c6470;
}

.lv-hint .dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 1px;
  margin: 0 2px 0 4px;
}

.lv-hint .dot.higher {
  background: rgba(240, 160, 80, 0.55);
}

.lv-hint .dot.primary {
  background: rgba(198, 120, 221, 0.6);
}

.lv-hint .dot.lower {
  background: rgba(90, 170, 240, 0.5);
}

.nest {
  font-style: normal;
  font-size: 9px;
  color: #6b7280;
  border: 1px solid #2c313a;
  border-radius: 2px;
  padding: 0 2px;
  margin-left: 3px;
}

/* ── 缠论中枢列表 ── */
.chan-pivots {
  margin-top: 8px;
  border-top: 1px solid #1e222a;
  padding-top: 6px;
}

.pivot-hd,
.pivot-row {
  display: grid;
  grid-template-columns: 1fr 36px 52px;
  gap: 6px;
  font-size: 11px;
  padding: 1px 0;
}

.pivot-hd {
  color: #5c6470;
  font-size: 10px;
}

.pivot-range {
  font-variant-numeric: tabular-nums;
  color: #d1d4dc;
}

.pivot-strokes {
  text-align: right;
  color: #9aa3b0;
  font-variant-numeric: tabular-nums;
}

.pivot-state {
  text-align: right;
  color: #6b7280;
}

.pivot-state.live {
  color: #f0b90b;
}

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
  flex-wrap: wrap;
  gap: 6px 8px;
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
  font-size: 11px;
  margin-top: 4px;
  color: #6b7280;
  column-gap: 7px;
  row-gap: 2px;
  flex-wrap: wrap;
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

.cred-badge {
  flex: 0 0 auto;
  margin-left: 8px;
  padding: 0 6px;
  border-radius: 3px;
  font-size: 11px;
  line-height: 16px;
  border: 1px solid var(--color-border-2);
  color: var(--color-text-3);
  white-space: nowrap;
}

.score-badge {
  font-size: 12px;
  padding: 1px 6px;
  border-radius: 4px;
  background: #23262e;
  color: #9aa2ad;
  cursor: default;
}

.cred-badge.good {
  border-color: rgba(0, 180, 42, 0.45);
  color: rgb(var(--green-6));
}

.cred-badge.mid {
  border-color: rgba(255, 170, 0, 0.45);
  color: rgb(var(--orange-6));
}

.cred-badge.weak {
  border-color: rgba(245, 63, 63, 0.4);
  color: rgb(var(--red-6));
}

.cred-badge.unknown {
  border-style: dashed;
}

.cred-tip {
  font-size: 12px;
  line-height: 18px;
}

.cred-tip-title {
  font-weight: 600;
  margin-bottom: 4px;
}

.cred-tip-foot {
  margin-top: 4px;
  color: var(--color-text-3);
}

.signal-sub .lag.good {
  color: #26a69a;
}

.signal-sub .lag.mid {
  color: #f0b90b;
}

.signal-sub .lag.weak {
  color: #ef5350;
}

.signal-sub .lag.good,
.signal-sub .lag.mid,
.signal-sub .lag.weak {
  font-weight: 600;
}

.signal-row {
  cursor: pointer;
  padding-bottom: 4px;
}

.signal-row:hover .signal-head {
  filter: brightness(1.2);
}

.signal-head,
.signal-sub,
.signal-meta {
  display: flex;
  align-items: center;
}

/* 关键纪律：项与项之间可以换行，项内部绝不允许折行（此前"缠论 3卖"被压成竖排就是这个原因） */
.signal-head > *,
.signal-sub > *,
.signal-meta > * {
  white-space: nowrap;
  flex-shrink: 0;
}

.flex-spacer {
  flex: 1 1 8px;
}

.signal-head {
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
  row-gap: 2px;
}

.signal-sub {
  gap: 6px;
  margin-top: 4px;
}

.signal-time {
  color: #6b7280;
  font-size: 10px;
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



.panel-empty {
  margin-top: 40px;
}
</style>

.tbl-note.gate .v { color: #9aa3b0; }
.tbl-note.gate i { font-style: normal; color: #6b7280; }
