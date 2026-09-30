// 提醒规则核对：直接驱动**线上模块** frontend/src/composables/useSignalNotify.ts
// （不是复制一份逻辑），校验去重/时效/冷却/首次不打扰四条口径。
// 运行：bun tools/verify/notify-rules.ts（需在仓库根目录；模块内的 'vue' 由 frontend/node_modules 解析）
type FakeNotification = { title: string; body?: string; tag?: string }

// 在 import 之前打桩：模块加载时会读 localStorage 与 Notification
const store = new Map<string, string>([['mdk.notify', '1']])
;(globalThis as any).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => void store.set(k, v),
}
const fired: FakeNotification[] = []
;(globalThis as any).Notification = class {
  static permission = 'granted'
  static requestPermission = async () => 'granted'
  constructor(title: string, opts?: { body?: string; tag?: string }) {
    fired.push({ title, body: opts?.body, tag: opts?.tag })
  }
}

const { useSignalNotify } = await import('../../frontend/src/composables/useSignalNotify.ts')
const { notifyEnabled, setEnabled, primeSeen, notify, buildPayloads } = useSignalNotify()

let pass = 0, fail = 0
const check = (ok: boolean, name: string, detail = '') => {
  if (ok) { pass++; console.log(`  ✓ ${name}`) }
  else { fail++; console.log(`  ✗ ${name}${detail ? ' — ' + detail : ''}`) }
}
const sig = (over: Partial<Parameters<typeof notify>[0][number]> = {}) => ({
  symbol: 'BTCUSDT', baseAsset: 'BTC', quoteAsset: 'USDT', interval: '1h',
  side: 'buy' as const, time: 1_790_000_000, note: '[3买] 回抽不破', price: 83000, stopPrice: 82800,
  ...over,
})
const NOW = 1_790_000_000_000   // 与 sig.time 同一时刻（毫秒）

console.log('提醒规则核对（驱动线上模块）')

// ① 首次标记已见后不再打扰
primeSeen([sig()])
check(notify([sig()], NOW).length === 0, '首次标记已见后，同一信号不再提醒')

// ② 新信号 → 提醒，且标题/正文/标签正确
const fresh = sig({ time: 1_790_000_600, note: '[3卖] 跌破中枢下沿' , side: 'sell' as const })
const payloads = notify([fresh], NOW)
check(payloads.length === 1 && fired.length === 1, '新信号产生一条通知')
check(fired[0]?.title === 'BTC/USDT 1h 卖点', '通知标题为 “标的 周期 买点/卖点”', String(fired[0]?.title))
check((fired[0]?.tag ?? '') === 'BTCUSDT-1h-1790000600-sell', '通知标签含标的/周期/时间/方向', String(fired[0]?.tag))
check((fired[0]?.body ?? '').includes('止损参考'), '正文含价格与止损参考')

// ③ 同一信号重复推送 → 只提醒一次
check(notify([fresh], NOW).length === 0, '同一信号重复推送不再提醒')

// ④ 同向冷却：刚提醒过 sell，同标的同周期的下一个 sell 在 30 分钟内被拦下
const sameDir = sig({ time: 1_790_000_700, side: 'sell' as const, note: '[3卖] 再次跌破' })
check(notify([sameDir], NOW + 10 * 60_000).length === 0, '同向 10 分钟后被冷却拦下')
const otherDir = sig({ time: 1_790_000_800, side: 'buy' as const, note: '[3买] 反向' })
check(notify([otherDir], NOW + 11 * 60_000).length === 1, '反向信号不冷却（多空翻转更值得知道）')
const later = sig({ time: 1_790_002_000, side: 'sell' as const, note: '[1卖] 背驰' })
check(notify([later], NOW + 31 * 60_000).length === 1, '超过 30 分钟冷却后放行')

// ⑤ 时效：6 小时前的信号不提醒
const stale = sig({ time: 1_790_000_000 - 7 * 3600, note: '[2买] 回抽确认' })
check(notify([stale], NOW).length === 0, '超过 6 小时的信号不提醒')

// ⑥ 关掉开关后不再提醒，但仍记为已见（重开后不补弹）
setEnabled(false)
const off = sig({ time: 1_790_000_800, note: '[3买] 贴边回抽' })
check(notify([off], NOW + 40 * 60_000).length === 0, '开关关闭时不提醒')
setEnabled(true)
check(notify([off], NOW + 60 * 60_000).length === 0, '重新开启后不补弹已见信号')
check(notifyEnabled.value === true, '开关状态可读写')

// ⑦ buildPayloads 是纯函数：不写已见集合，可预演
const preview = buildPayloads([sig({ time: 1_790_001_000, note: '[3卖] 测试' })], NOW + 90 * 60_000)
check(preview.length === 1 && preview[0].cooldownKey === 'BTCUSDT|1h|buy', 'buildPayloads 返回冷却键供复核')

console.log(`\n结果：通过 ${pass}，失败 ${fail}`)
process.exit(fail === 0 ? 0 : 1)
