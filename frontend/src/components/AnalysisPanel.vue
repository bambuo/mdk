<script setup lang="ts">
import { computed, ref } from 'vue'
import type { AnalysisResult, EvidenceBucket, PriceLevel, SignalStatsResponse, TradeSignal } from '../types'
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

/** 强度分文本：1 位小数；未测试的客观锚点为 0，显示为 —（星标会封顶，分数不会） */
function levelScore(l: PriceLevel): string {
  const score = l.score ?? 0
  return score > 0 ? score.toFixed(1) : '—'
}

/**
 * 位点来源标签：摆动点是基准口径，不再重复标注。
 * `?? []` 是版本错位的护栏：前端热更新会先于后端重启生效，旧响应里没有 sources 字段。
 */
function levelTags(l: PriceLevel): string[] {
  return (l.sources ?? []).filter(s => s !== '摆动点')
}

/** 位点悬停说明：强度构成与来源（回答"这条线凭什么在这里"） */
function levelTitle(l: PriceLevel): string {
  const parts = [`强度 ${(l.score ?? 0).toFixed(1)}`]
  parts.push(l.touches > 0 ? `触碰 ${l.touches} 次` : '尚未被触碰（客观锚点）')
  if (l.reactionAtr != null) parts.push(`平均反应 ${l.reactionAtr.toFixed(1)}×ATR`)
  if (l.ageBars != null) parts.push(`最近触碰在 ${l.ageBars} 根前`)
  parts.push(`来源：${(l.sources ?? []).join('/')}`)
  return parts.join(' · ')
}

/** 位点的价格基础（合约默认用标记价：强平插针不算破位） */
const levelsBasisNote = computed(() =>
  props.analysis?.levelsBasis === 'mark' ? '合约按标记价计算（防插针）' : '',
)

/** 级别共振筛选：all=全部，aligned=只看"级别共振"信号 */
const confluenceOnly = ref(false)

/** 共振分组绩效（来自 byConfluence） */
const confluenceStats = computed(() => props.stats?.byConfluence ?? [])

/** 次级别数据覆盖提示：次级别K线不足整个显示窗口时给出说明（结构位置面板底部展示） */
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
  if (p.insideHigher === true) parts.push('位于高级别中枢区域内（大级别震荡中）')
  if (p.insideHigher === false) parts.push('不在高级别中枢区域内（大级别之外）')
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
 * 结构位置（确定性）：把后端的结构度量转成可扫读的事实。
 * 只做格式化，不含任何推断——同一行情任何时候显示同一结果。
 */
const position = computed(() => props.analysis?.chan?.position ?? null)

/** 关注度（后端确定性判定；旧后端可能为空） */
const attention = computed(() => props.analysis?.chan?.position?.attention ?? null)

/** 五条规则：与后端 AttentionRules.Rules 同源（界面文案副本，改口径须两处同改） */
const attentionRules = [
  '① 结构链：包含处理 → 分型 → 笔 → 中枢 → 背驰 → 买卖点；笔≥3 构成中枢（多空成本区）',
  '② 只做三类位置：中枢边缘回抽不破（3类，顺势）／趋势末端背驰（1类，逆势最难）／一类后首次回抽（2类）',
  '③ 确认才作数：只有已确认的笔产生买卖点；信号记在确认那根K线，你看到时价格已离开参考点',
  '④ 止损=结构失效位：价格回到买卖点所依据的参考极值，前提即不成立',
  '⑤ 三不做：中枢内震荡不猜方向；距中枢 >2×ATR 的趋势中段不追；已走 >1R 不进场',
]

function zoneLabel(zone: string | null | undefined): string {
  if (zone === 'above') return '中枢上方'
  if (zone === 'below') return '中枢下方'
  if (zone === 'in') return '中枢内'
  return '无中枢'
}

function zoneClass(zone: string | null | undefined): string {
  if (zone === 'above') return 'above'
  if (zone === 'below') return 'below'
  if (zone === 'in') return 'inside'
  return 'none'
}

function strokeLabel(p: { strokeDirection: string; strokeConfirmed: boolean }): string {
  if (p.strokeDirection === 'none') return '尚无已成形笔'
  return `${p.strokeDirection === 'up' ? '向上笔' : '向下笔'}${p.strokeConfirmed ? ' 已确认' : ' 未确认（可能延伸）'}`
}

/** 回撤位置的人话：三种情形分开说，避免"−2.9% 处"这类反直觉表述 */
function retraceLabel(p: { retracePct: number | null }): string {
  if (p.retracePct == null) return '—'
  const r = p.retracePct * 100
  if (r < 0) return `已越过端点 ${Math.abs(r).toFixed(1)}%（延伸中）`
  if (r > 100) return `全幅回撤 ${(r - 100).toFixed(1)}%（本笔或将被破坏）`
  return `位于本笔 ${(100 - r).toFixed(1)}% 处（自极值端回撤 ${r.toFixed(1)}%）`
}

function crossLabel(c: string): { text: string; cls: string } {
  if (c === 'aligned') return { text: '各级别同侧（一致）', cls: 'aligned' }
  if (c === 'mixed') return { text: '各级别归属分歧', cls: 'mixed' }
  if (c === 'single') return { text: '仅本级别有中枢', cls: 'none' }
  return { text: '各级别均无中枢', cls: 'none' }
}

const levelName = (level: string) => (level === 'higher' ? '高级别' : level === 'lower' ? '次级别' : '本级别')

/**
 * 证据强度徽章：按 (来源, 类别) 到台账经验表里查同语境的**实际表现**，
 * 不做加权、不给"置信度分数"——只陈述样本量、胜率区间与扣费后为正比例；
 * 波次不足时明确显示"样本不足"（口径见后端 EvidenceRules）。
 */
const evidenceMap = computed(() => {
  const map = new Map<string, EvidenceBucket>()
  for (const bucket of props.analysis?.evidence ?? []) {
    map.set(bucket.kind ?? '', bucket)
  }
  return map
})

function bucketOf(sig: TradeSignal): EvidenceBucket | null {
  return evidenceMap.value.get(sig.kind ?? '') ?? null
}

function evidenceText(sig: TradeSignal): string {
  const bucket = bucketOf(sig)
  if (!bucket) return '无历史样本'
  if (!bucket.sufficient) return `样本不足 · ${bucket.nEpisodes}波`
  const halfWidth = Math.round((bucket.winRateHigh - bucket.winRateLow) * 50)
  return `证据 ${Math.round(bucket.winRate * 100)}%±${halfWidth} · ${bucket.nEpisodes}波`
}

function evidenceClass(sig: TradeSignal): string {
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
    <!-- 关注度（置顶）：页面第一眼回答"此刻是否值得看单"——由结构位置确定性推出 -->
    <section v-if="attention" class="card attn-card" :class="attention.level">
      <div class="attn-head">
        <span class="attn-dot" />
        <span class="attn-headline">{{ attention.headline }}</span>
        <span class="flex-spacer" />
        <a-popover trigger="hover" position="left">
          <span class="attn-rules">规则速查 ⓘ</span>
          <template #content>
            <div class="cred-tip">
              <div class="cred-tip-title">缠论（本系统口径）五条规则</div>
              <div v-for="(r, i) in attentionRules" :key="i" class="rule-line">{{ r }}</div>
              <div class="cred-tip-foot">规则只界定"结构上是否到了决策位"；本项目实测扣费后无统计优势，不构成投资建议</div>
            </div>
          </template>
        </a-popover>
      </div>
      <ul class="attn-facts">
        <li v-for="(f, i) in attention.facts" :key="i">{{ f }}</li>
      </ul>
      <div class="attn-body">
        <span class="k">为什么</span><span class="v">{{ attention.why }}</span>
        <span class="k">怎么做</span><span class="v">{{ attention.how }}</span>
        <template v-if="attention.dont">
          <span class="k">别做</span><span class="v warn">{{ attention.dont }}</span>
        </template>
      </div>
    </section>

    <details class="detail-fold">
      <summary>细节（结构位置 / 买卖点 / 趋势 / 点位）</summary>

    <!-- 结构位置（确定性）：页面第一眼回答"价格在结构的哪里" -->
    <section v-if="analysis.chan" class="card pos-card">
      <h3 class="card-title">
        结构位置
        <span class="card-sub">{{ analysis.interval.toUpperCase() }} · 确定性输出</span>
      </h3>

      <template v-if="position">
        <!-- 首行：一句话结论（中枢归属 + 笔的位置） -->
        <p class="pos-headline">
          <span class="pos-zone" :class="zoneClass(position.pivotZone)">{{ zoneLabel(position.pivotZone) }}</span>
          <span class="pos-sep">·</span>
          <span class="pos-stroke">{{ strokeLabel(position) }}</span>
        </p>

        <!-- 四行确定性事实 -->
        <div class="pos-grid">
          <span class="k">笔</span>
          <span class="v">
            {{ strokeLabel(position) }}
            <i>已运行 {{ position.strokeBars }} 根</i>
          </span>

          <span class="k">笔内位置</span>
          <span class="v">{{ retraceLabel(position) }}</span>

          <span class="k">中枢</span>
          <span class="v">
            <template v-if="position.pivotZg != null">
              {{ formatPrice(position.pivotZd!) }} ~ {{ formatPrice(position.pivotZg!) }}
              <i>{{ position.pivotStrokes }} 笔 · 已 {{ position.pivotBars }} 根</i>
            </template>
            <template v-else>—</template>
          </span>

          <span class="k">距边界</span>
          <span class="v">
            <template v-if="position.edgeDistancePct != null">
              {{ (position.edgeDistancePct * 100).toFixed(2) }}%
              <i v-if="position.edgeDistanceAtr != null">（{{ position.edgeDistanceAtr.toFixed(1) }}×ATR）</i>
            </template>
            <template v-else>中枢内</template>
          </span>

          <span class="k">结构参照</span>
          <span class="v">
            <template v-if="position.lastKind">
              {{ position.lastKind }}
              <i>{{ position.barsSinceLastSignal === 0 ? '本根' : `${position.barsSinceLastSignal} 根前` }}</i>
              <template v-if="position.stopReferencePrice != null">
                · 止损参考 {{ formatPrice(position.stopReferencePrice) }}
                <i v-if="position.distanceToStopReferencePct != null">距现价 {{ (position.distanceToStopReferencePct * 100).toFixed(2) }}%</i>
              </template>
              <template v-if="position.invalidationPrice != null">
                <i>（结构失效位 {{ formatPrice(position.invalidationPrice) }}）</i>
              </template>
            </template>
            <template v-else>窗口内暂无买卖点</template>
          </span>
        </div>

        <!-- 级别对照：同一现价在各级别中枢的归属 -->
        <div v-if="position.levels.length" class="pos-levels">
          <div class="lv-hd"><span>级别</span><span>中枢</span><span class="ta-r">归属</span></div>
          <div v-for="lv in position.levels" :key="lv.level" class="lv-row">
            <span class="lv-name" :class="lv.level">{{ levelName(lv.level) }} <i>{{ lv.interval }}</i></span>
            <span class="lv-count">{{ lv.pivotZg != null ? `${formatPrice(lv.pivotZd!)} ~ ${formatPrice(lv.pivotZg)}` : '无中枢' }}</span>
            <span class="ta-r pos-zone-sm" :class="zoneClass(lv.zone)">
              {{ zoneLabel(lv.zone) }}<template v-if="lv.distancePct != null"><i> {{ (lv.distancePct * 100).toFixed(2) }}%</i></template>
            </span>
          </div>
          <p class="lv-hint" :class="crossLabel(position.crossLevel).cls">
            {{ crossLabel(position.crossLevel).text }}
            <template v-if="lowerCoverageHint"> · {{ lowerCoverageHint }}</template>
          </p>
        </div>
      </template>
      <div v-else class="chan-grid">
        <span class="k">当前笔</span>
        <span class="v">{{ analysis.chan.lastStrokeDirection === 'up' ? '向上笔' : analysis.chan.lastStrokeDirection === 'down' ? '向下笔' : '—' }}<i>{{ analysis.chan.lastStrokeConfirmed ? '已确认' : '未确认' }}</i></span>
        <span class="k">最新中枢</span>
        <span class="v"><template v-if="analysis.chan.pivotZg != null">{{ formatPrice(analysis.chan.pivotZd!) }} ~ {{ formatPrice(analysis.chan.pivotZg!) }}<i>{{ analysis.chan.pivotStrokes }} 笔</i></template><template v-else>—</template></span>
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
        缠论买卖点
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
          <!-- 样本构成：实时样本只是事实计数（"可实盘"判据已废弃，系统不给出交易授权） -->
          <div class="tbl-note gate">
            <span class="k">样本构成</span>
            <span class="v">窗口 {{ stats.windowEpisodes }} 波，其中实时落库 {{ stats.realtimeEpisodes }} 波<i>（其余为历史回算）</i></span>
          </div>
          <div class="tbl-foot">{{ stats.windowBasis }} · 持有 12 根 · 已扣费（现货往返 0.2%） · 门槛：独立波次 ≥30 且 t≥2 且扣费后为正 ≥50% 且中位超额 >0 且单标的 ≤50%</div>
        </div>
      </div>

      <!-- 筛选：单行，弱化视觉权重 -->
      <div class="sigs-controls">
        <div class="seg">
          <button type="button" :class="{ on: !confluenceOnly }" @click="confluenceOnly = false">全部</button>
          <button type="button" :class="{ on: confluenceOnly }" @click="confluenceOnly = true"
                  title="只显示前 1 根高级别K线内出现同向高级别缠论买卖点的信号（级别共振）">只看共振</button>
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
                <span class="cred-badge" :class="evidenceClass(sig)">{{ evidenceText(sig) }}</span>
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
        <span v-if="levelsBasisNote" class="card-sub">{{ levelsBasisNote }}</span>
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
          <span v-if="levelTags(level).length" class="level-tags">
            <span v-for="tag in levelTags(level)" :key="tag" class="level-tag">{{ tag }}</span>
          </span>
          <span class="level-dist">{{ formatPct(level.distancePct) }}</span>
          <span class="level-strength" :title="levelTitle(level)">{{ levelScore(level) }}</span>
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
          <span v-if="levelTags(level).length" class="level-tags">
            <span v-for="tag in levelTags(level)" :key="tag" class="level-tag">{{ tag }}</span>
          </span>
          <span class="level-dist">{{ formatPct(level.distancePct) }}</span>
          <span class="level-strength" :title="levelTitle(level)">{{ levelScore(level) }}</span>
        </div>
        <div v-if="!supports.length" class="level-empty">下方暂无明显支撑</div>
      </div>
    </section>
    </details>

  </template>
  <a-empty v-else description="暂无分析数据" class="panel-empty" />
</template>

<style scoped>
/* ── 关注度卡（置顶） ── */
.attn-card {
  border-left: 3px solid #2a2f38;
}

.attn-card.focus { border-left-color: #26a69a; }
.attn-card.watch { border-left-color: #f0b90b; }
.attn-card.chase { border-left-color: #ef5350; }
.attn-card.none,
.attn-card.wait { border-left-color: #3a4048; }

.attn-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 600;
}

.attn-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #3a4048;
  flex: 0 0 auto;
}

.attn-card.focus .attn-dot { background: #26a69a; }
.attn-card.watch .attn-dot { background: #f0b90b; }
.attn-card.chase .attn-dot { background: #ef5350; }

.attn-headline {
  flex: 0 1 auto;
  min-width: 0;
}

.attn-rules {
  font-size: 11px;
  font-weight: 400;
  color: #6b7280;
  border-bottom: 1px dashed #3a4048;
  cursor: help;
  white-space: nowrap;
}

.attn-facts {
  margin: 6px 0 8px;
  padding-left: 14px;
  font-size: 11px;
  color: #9aa3b0;
  line-height: 1.7;
}

.attn-body {
  display: grid;
  grid-template-columns: 46px 1fr;
  row-gap: 4px;
  column-gap: 8px;
  font-size: 12px;
}

.attn-body .k { color: #6b7280; }
.attn-body .v { color: #c3c8d0; }
.attn-body .v.warn { color: #f0b90b; }

.rule-line {
  line-height: 1.8;
}

/* ── 细节折叠区 ── */
.detail-fold > summary {
  margin: 2px 0 10px;
  padding: 5px 8px;
  font-size: 11px;
  color: #6b7280;
  border: 1px dashed #23262e;
  border-radius: 4px;
  cursor: pointer;
  list-style: none;
}

.detail-fold > summary::-webkit-details-marker { display: none; }

.detail-fold > summary::before {
  content: '▸ ';
}

.detail-fold[open] > summary::before {
  content: '▾ ';
}

.detail-fold > summary:hover {
  color: #9aa3b0;
  border-color: #3a4048;
}

/* ── 结构位置（确定性面板） ── */
.pos-card .pos-headline {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0 0 8px;
  font-size: 13px;
  font-weight: 600;
  flex-wrap: wrap;
}

.pos-zone {
  padding: 1px 7px;
  border-radius: 3px;
  border: 1px solid #2a2f38;
  font-weight: 600;
}

.pos-zone.above {
  color: #26a69a;
  border-color: rgba(38, 166, 154, 0.45);
  background: rgba(38, 166, 154, 0.08);
}

.pos-zone.below {
  color: #ef5350;
  border-color: rgba(239, 83, 80, 0.45);
  background: rgba(239, 83, 80, 0.08);
}

.pos-zone.inside {
  color: #f0b90b;
  border-color: rgba(240, 185, 11, 0.4);
  background: rgba(240, 185, 11, 0.06);
}

.pos-zone.none {
  color: #6b7280;
}

.pos-sep {
  color: #3a4048;
}

.pos-stroke {
  color: #9aa3b0;
  font-weight: 400;
}

.pos-grid {
  display: grid;
  grid-template-columns: 62px 1fr;
  row-gap: 5px;
  column-gap: 8px;
  font-size: 12px;
}

.pos-grid .k {
  color: #6b7280;
}

.pos-grid .v {
  color: #c3c8d0;
}

.pos-grid .v i {
  font-style: normal;
  color: #6b7280;
  margin-left: 4px;
}

.pos-levels {
  margin-top: 10px;
  padding-top: 8px;
  border-top: 1px solid #1b1e24;
}

.pos-zone-sm {
  font-size: 11px;
}

.pos-zone-sm.above { color: #26a69a; }
.pos-zone-sm.below { color: #ef5350; }
.pos-zone-sm.inside { color: #f0b90b; }
.pos-zone-sm.none { color: #6b7280; }

.lv-hint.aligned { color: #26a69a; }
.lv-hint.mixed { color: #f0b90b; }
.lv-hint.none { color: #6b7280; }

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
  width: 40px;
  text-align: right;
  color: #f0b90b;
  font-size: 11px;
  flex: 0 0 auto;
}

/* 客观锚点标签（前日高/整数关口/日VWAP 等）：说明这条线凭什么在这里 */
.level-tags {
  display: flex;
  gap: 3px;
  flex: 0 0 auto;
}

.level-tag {
  font-size: 10px;
  line-height: 15px;
  padding: 0 4px;
  border-radius: 3px;
  color: #9aa3b0;
  background: rgba(255, 255, 255, 0.06);
  white-space: nowrap;
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
.tbl-note.gate .v { color: #9aa3b0; }
.tbl-note.gate i { font-style: normal; color: #6b7280; }
</style>
