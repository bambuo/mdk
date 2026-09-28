# MDK 交易分析工作台 — 实施计划

> 状态：已确认（2026-09-28）。本文件是实施基准，含用户追加的领域模型要求。

## 0. 已确认的决策
- **布局**：左图右栏（左 70% 图表区：K线主图 + RSI/MACD 副图；右 30% 分析栏：趋势卡片、关键点位、买卖信号三张卡片）。
- **数据链路**：C# 后端统一代理币安（REST 快照 + WebSocket 实时推送），BaseUrl/WS 域名可配置。
- **指标**：EMA(20/50/200)、RSI(Wilder 14)、MACD(12,26,9)、BOLL(20,2) 全部实现；趋势方向判定固定用 EMA200 + ADX/DMI（后端计算）；买卖点信号由 EMA 交叉 / RSI 反转 / MACD 交叉自动生成，每个信号附 2×ATR 止损参考价。
- BOLL 仅作主图通道显示，不产生信号。

## 0.1 领域模型约定：交易对值对象（用户追加要求）- 全项目**统一用 `TradingPair` 值对象表示交易对**，禁止在业务代码中传裸字符串。
- 值对象为不可变 `readonly record struct`（按值相等），内含两个字段：
  - `BaseAsset` —— **交易币**（被交易的标的币，如 BTC/USDT 中的 BTC）
  - `QuoteAsset` —— **计价币**（标价货币，如 BTC/USDT 中的 USDT）
- 两种字符串形态：
  - `Symbol`：币安连写格式 `BTCUSDT`（对外请求、WS 订阅、JSON 序列化均用它）
  - `Display`：展示格式 `BTC/USDT`（前端展示用）
- **拆分权威来源**：币安 `exchangeInfo`（`SymbolCatalog` 缓存后提供 `ResolveAsync(symbol)` 校准交易币/计价币）；本地用已知计价币后缀（USDT/USDC/FDUSD/BTC/ETH…按长度优先）做快速解析入口，解析失败或交易所不存在即拒绝。
- 贯穿位置：REST 接口参数（Minimal API 自定义绑定 `BindAsync`，参数即 `TradingPair`）、`BinanceRestClient`、`KlineStreamService`、`AnalysisResult`、`/api/symbols` 返回（含 base/quote 拆分，前端显示与搜索用）。
- 配套单元测试：连写解析、大小写不敏感、长计价币后缀优先（如 `ETHFDUSD` → ETH/FDUSD）、未知计价币拒绝、值相等性、JSON 往返。

## 0.2 双市场支持（用户追加要求，2026-09-28 完成）
- 用户问「为什么搜不到 LIT 合约」→ 定位为两个原因：① 此前只接现货 API；② 现货 LITUSDT 状态为 `BREAK`（项目改名 Heima），被只收 `TRADING` 的过滤器排除。用户选择方案 2「双市场支持」。
- **市场类型 `MarketKind`（Spot / Futures）贯穿全栈**：REST 路径前缀（`/api/v3` vs `/fapi/v1`）、WS 域名（`stream.binance.com` vs `fstream.binance.com`）、交易对目录缓存、K线流上游 key、分析结果与 WS 消息。
- 所有接口新增 `market=spot|futures` 参数（缺省 spot）；WS 消息体与 JSON 均含 `market` 字段（枚举按小写序列化）。
- 前端顶栏新增「现货 / 合约」切换，切换时重载目录、K线、分析与 WS 连接；占位文案与市场标记随市场变化。
- 顺带修复：现货 `exchangeInfo` 已膨胀到 ~17MB，下载约 12.8s 触顶原 15s HttpClient 超时 → 启用 gzip/br 自动解压 + 超时放宽至 60s。
- 网络适配：本机环境下 `fstream.binance.com`（合约 WS）可握手但不推数据（现货 WS 正常、合约 REST 正常）→ 为 WS 上游增加 **REST 轮询兜底**（静默 > 8s 后每 5s 轮询 REST 最新K线并分发，WS 恢复自动停用）；顶栏价格徽标改由 WS 实时价驱动。

## 0.3 K线实时性修复（用户反馈"切换交易对/周期后价格线不实时更新"，2026-09-28 完成）
排查结论：后端每次切换都在正常推送（实测新流首条增量 0.2~5.2s 内到达），缺陷在前端与订阅生命周期上，共三处：
1. **上游空闲关闭竞态（后端）**：K线流空闲 30s 触发关闭时，若新订阅在"已决定关闭、尚未从字典移除"的窗口内到达，会复用这条即将关闭的上游 → 订阅方永远收不到数据（前端却显示"实时连接"）。修复：`Upstream.IsClosing` 标记（锁内置位），`Subscribe` 拒复用在关闭中的上游；`Closed` 回调按引用校验移除，避免误删新建上游。
2. **切换时图表被卸载重建（前端）**：`v-if="!loading"` 使图表每次切换都重建 → 加载期间的实时增量全部丢弃、缩放被重置；冷启动 exchangeInfo（约 17MB）耗时长时，图表长时间停在快照。修复：图表常驻挂载 + 加载遮罩，加载期间跳过增量写入（该窗口由随后到达的REST快照覆盖，避免把新标的数据画到旧图上）。
3. **交易对选择器实为非受控（前端）**：Arco `a-select` 无 `value` 属性（仅 `model-value`），原 `:value` 被忽略 → 程序自动改选标的时显示不同步（显示 A、实际加载 B）。修复为 `model-value`。
4. 顺带：默认标的过滤锚定币（切市场时旧逻辑会落到成交额第一的 USDC/USDT，价格恒定形成"死图"），优先 BTC；WS 增量加乱序忽略与断层检测（断层触发重新拉快照）；指标序列与K线改为**尾部对齐**（/api/klines 与 /api/analysis 两次请求可能错开一根）；顶栏新增推送新鲜度指示（"实时连接 · 实时 / Ns 前"，>15s 变黄）。

## 0.4 信号可用性 v2（用户目标"提升信号可用性"，2026-09-28 完成）
依据两轮事后审计（可靠性过滤器对照 + 即时性速度前沿，3 币 × 1h/4h × 1000 根，详见会话记录）落地：
1. **收盘确认分级（去重绘）**：确认信号只在已收盘K线上判定（不可撤销）；最后一根未收盘K线上的交叉输出为「盘中预警」（`isConfirmed=false`，随收盘重算自动转正或消失）。即时性与可靠性的权衡显式化为两级机制。
2. **冷却去重**：同源同向 `CooldownBars`（默认 6）根内重复触发不重复报（MACD 信号中趋势中段的无效交叉占主体）。
3. **多周期共振标记**：每信号带 `trendAligned`（信号方向 vs 高一期 EMA50，取信号时刻已收盘的高周期K线）。实测顺大势组止损命中率显著更低（BTC 1h：27% vs 45%），前端提供「只看顺大势」开关。
4. **EMA 参数提速（仅 1h）**：1h 信号改用 EMA(10,30) 交叉（显示用 EMA20/50/200 不变，另发 `emaSigFast/emaSigSlow` 虚线）。依据：唯一统计显著项（胜率 58.3% vs 54.4%，超额 +0.40%，t=2.21，信号前已走减半）；4h 提速为负贡献故不启用。可经 `Signal:UseFastEmaOn1h` 关闭。
5. **信号落库 + 事后绩效闭环（P0）**：每个信号（确认与预警分开记键）追加写入 `backend/Mdk.Api/data/signals.jsonl`（已 gitignore）；`SignalOutcomeService` 每 60s 扫描持有期（12 根）已满的信号，回填收益/超额（剔除同期漂移）/最大浮盈浮亏/2×ATR 止损命中/扣费后正负（现货往返 0.2%、合约 0.1%）；`GET /api/signal-stats?market&symbol&interval&days` 返回分来源绩效；前端信号卡显示来源绩效芯片（如 "MACD 56% n=27"）。
6. 已知局限：统计自部署起积累（冷启动时面板显示"积累中"）；n<40 的分组不可作结论；样本内调参必须经滚动样本外验证（待 P2）。

## 0.5 信号可用性 v3：统计纪律与状态维度（2026-09-28 完成）
背景：v2 上线后对生产日志（系统实际发出并事后评估的信号）做主分析，发现 ① 建议的「顺大势」过滤在生产样本中方向相反（早期 n=89/146：顺大势 44.9% vs 逆大势 54.8%）② 小样本结论极不稳定（RSI 在 n=46 时扣费后为正 46% 评为最佳，n=73 时跌到 37% 变最差）③ 信号高度时间聚集导致 t 值虚高。据此落地：
1. **市场状态随信号记录**：每条信号带 ADX / ATR%（波动率）/ 布林带宽，写入日志；`/api/signal-stats` 新增 `byRegime`（趋势市 ADX≥25 / 过渡 / 震荡市 ADX<20）分组——用于验证"过滤器是否只在特定市场状态有效"。
2. **波次（episode）口径**：同币种/周期/方向、间隔 24 根以内的信号聚为一波，统计同时给出原始 n 与波次数。修正"信号时间聚集 → 独立性虚高"的问题（例：MACD n=175 实际仅 96 波）。
3. **准入分级**：按样本量 + 扣费后为正比例自动分级（可参考 / 仅观察 / 不达标 / 样本不足，样本 <30 一律"样本不足"）。前端芯片直接显示分级，避免把噪音当结论。
4. **实验性「结构确认」信号源**：价格触及由**已确认历史摆动点**构成、且触碰 ≥3 次的支撑/阻力并有效收回（收回幅度 ≥0.1×ATR）→ 产生信号。严格无未来函数（单元测试用截断数据集验证）；预期即时性优于均线交叉。首版宽松规则（≥2 次触碰）产生信号过密（BTC 1h 500 根 54 条），已收紧并清理该批样本。
5. **关注列表定时分析**（`WatchlistAnalysisService`）：每 120s 重新分析最近被请求的最多 10 个 (市场,币种,周期) 组合。价值：盘中预警只在盘中计算时产生，此前仅浏览器打开才有样本（生产日志预警数为 0）；此服务使样本持续积累，评估量在数分钟内由 235 增至 351。
6. 前端：共振筛选改为**三态**（全部/顺大势/逆大势）并标注"共振无显著差异"（样本增至数百后顺/逆差异消失，49% vs 50%）；新增来源筛选（可单独隐藏实验来源）；每信号显示状态标签（趋势市/震荡市 ADX、波动率）。
7. **界面专业化重构**（用户反馈"新增布局太丑、不专业"）：信号卡的统计信息默认收起为单行摘要（`近90天 · N例/M波 · 整体分级`），展开后是**对齐的紧凑数据表**（来源 / 例·波 / 胜率 / 扣费 / 分级），替代原先的胶囊标签堆叠；筛选压缩为单行（自绘分段控件 + 紧凑多选）；信号行改为弱化的色块买/卖标记 + 点分隔的元信息（`已确认 · 趋势 ADX27 · 波动 0.47% · 逆大势`）；移除全部多余边框与等权噪点，对齐交易终端的信息层级做法。
8. 统计口径修正：同一信号可能先以「盘中预警」、后以「收盘确认」各记一条日志，`/api/signal-stats` 现按信号本体去重（优先保留确认版本），避免样本量虚增污染绩效与波次口径。

**当前结论（351 条评估、76 波）**：全部来源分级为"仅观察"（扣费后为正 37%-54%），尚无"可参考"来源；状态分组显示动量类信号在震荡市表现优于趋势市（67% vs 40%，n 尚小）；RSI 样本翻转是最有价值的教训——**任何不足百例的结论都不可信，必须依赖波动窗口持续复盘**。

## 1. 目录结构（全新项目 /Users/johana/Desktop/mdk）
```
mdk/
├── PLAN.md                       # 本计划
├── backend/
│   ├── Mdk.Api/                  # ASP.NET Core (.NET 10, Minimal API)
│   │   ├── Program.cs
│   │   ├── Domain/               # TradingPair / SymbolInfo / MarketIntervals
│   │   ├── Models/               # Candle / KlineUpdate / DTO
│   │   ├── Endpoints/            # Symbols / Klines / Analysis / Ws
│   │   ├── Binance/              # BinanceRestClient + KlineStreamService(WS上游) + SymbolCatalog
│   │   ├── Indicators/           # 纯C#指标库（无第三方依赖）
│   │   │   ├── WilderSmoothing.cs  Rsi.cs  Atr.cs  AdxDmi.cs
│   │   │   ├── Ema.cs  Sma.cs  Macd.cs  BollingerBands.cs
│   │   ├── Analysis/             # TrendAnalyzer / SupportResistance / SignalEngine / AnalysisEngine
│   │   └── appsettings.json      # Binance:RestBaseUrl / WsBaseUrl
│   └── Mdk.Tests/                # 自带测试（零外部依赖，轻量断言运行器）
└── frontend/                     # Vue3 + Vite + TS（bun 管理依赖与脚本）
    └── src/
        ├── api/                  # REST client
        ├── composables/          # useSymbols/useKlines/useKlineSocket/useAnalysis
        ├── components/
        │   ├── TopToolbar.vue    # 币种搜索选择 + 周期切换 + 指标开关
        │   ├── ChartPanel.vue    # lightweight-charts v5
        │   ├── AnalysisPanel.vue # 趋势/点位/信号三卡片
        │   └── cards/            # TrendCard / LevelsCard / SignalsCard
        └── App.vue
```

## 2. 后端（C#）
### 2.1 Wilder 公式与指标库（本次核心）
- `WilderSmoothing.Rma(values, n)`：前 n 个 SMA 作种子，之后 `ma = (ma×(n−1)+x)/n`；样本不足返回空段（NaN）。全部派生指标基于它：
  - **RSI(14)**：涨/跌幅均值分别 Wilder 平滑 → RS → RSI。
  - **ATR(14)**：TR = max(H−L, |H−Cprev|, |L−Cprev|)，Wilder 平滑。
  - **+DI/−DI/ADX(14)**：按 Wilder 原书定义，ADX 是 DX 的二次 Wilder 平滑。
  - EMA（SMA 种子 + α=2/(n+1)）、SMA、MACD(12,26,9)、BOLL(20,2)（SMA ± 2σ，总体标准差）。
- 测试用 **.NET 自带机制**（用户要求，不依赖外部测试框架）：`Mdk.Tests` 为零 NuGet 依赖的控制台工程，内置轻量断言运行器（TestKit），`dotnet run --project backend/Mdk.Tests` 执行，退出码 0 = 全部通过。覆盖：手算固定样例对照（Wilder/RSI/ATR/ADX/MACD/BOLL）、样本不足边界、TradingPair 解析与 JSON 往返、趋势/点位/信号规则合成数据用例。

### 2.2 分析引擎（对应三个概念）
1. **趋势方向** `TrendAnalyzer`：价格 vs EMA200/EMA50、EMA50 vs EMA200 排列、+DI vs −DI、ADX 强度（≥25 为强趋势）逐项打分，输出 `LONG(做多)/SHORT(做空)/RANGE(观望)` + 0–100 置信分 + 中文理由列表。
2. **关键点位** `SupportResistance`：摆动高低点（±3 根K线分形），按 0.5×ATR 聚类去重，强度=触碰次数；相对最新价分为支撑(下方)/阻力(上方)，各取最近 5 个。
3. **买卖点** `SignalEngine`：EMA20×50 金叉/死叉、RSI 30/70 反转、MACD 金叉/死叉；每个信号含时间、方向、来源指标、价格、中文说明、止损参考价（买点 −2×ATR / 卖点 +2×ATR）。

### 2.3 币安接入
- REST：仅用无需密钥的 `/api/v3/klines`、`/api/v3/ticker/24hr`、`/api/v3/exchangeInfo`；默认 `https://api.binance.com`，备用 `https://data-api.binance.vision`，appsettings 可配；内存缓存（ticker 30s、exchangeInfo 1h）防超频。
- WS 上游 `KlineStreamService`：连接 `wss://stream.binance.com:9443`（备用 data-stream.binance.vision）订阅 `<symbol>@kline_<interval>`；自动重连（指数退避）、心跳保活；多浏览器标签共享同流（引用计数，空闲 30s 自动断开）。

### 2.4 API 面（JSON，camelCase，CORS 放开本地端口；后端端口 5099 避开 macOS 5000 占用）
- `GET /api/symbols?quote=USDT&limit=50` → 按 24h 成交额排序的交易对，元素含 `symbol / baseAsset / quoteAsset / lastPrice / priceChangePercent / quoteVolume`。
- `GET /api/klines?symbol=BTCUSDT&interval=1h&limit=500` → `{symbol, baseAsset, quoteAsset, interval, candles:[{time,o,h,l,c,v}]}`；interval 白名单 5m/15m/30m/1h/2h/4h/6h/12h/1d/3d/1w；limit ≤ 1000。
- `GET /api/analysis?symbol&interval&limit` → `{trend{direction,score,reasons}, levels[{kind,price,strength,distancePct}], signals[{time,side,source,price,note,stopPrice}], series{ema20,ema50,ema200,rsi14,atr14,bollUpper,bollMiddle,bollLower}, macd{dif,dea,hist}}`（未定义段为 null）。
- `WS /ws?symbol&interval` → 推送 `{type:"kline"}`（最新未收盘K线增量）与 `{type:"analysis"}`（收盘触发重算 + ≥20 秒节流盘中刷新）。

## 3. 前端（Vue3 + bun + Arco Design Vue + lightweight-charts v5）
- bun 管理依赖与脚本；Vite 代理 `/api`、`/ws` → `http://localhost:5099`；暗色主题默认（Arco dark）。
- 顶栏：币种下拉（可搜索、按成交额排序、显示 `BTC/USDT`）、周期切换（15m/30m/1h/4h/1d/1w）、指标开关组（EMA/RSI/MACD/BOLL/支撑阻力/买卖点）、WS 状态。
- 主图：K线 + 3 条 EMA + BOLL 通道 + 支撑/阻力价格线（绿支撑/红阻力，标题含强度命名 R1/S1…）+ 买卖信号 markers（▲▼）。
- 副图（v5 panes）：RSI（30/70 参考线）、MACD（柱 + DIF/DEA）。
- 右栏三卡片：趋势（方向大标签+置信分+理由）、关键点位（距现价%、强度、点击垂直定位）、买卖信号（时间线，点击定位，显示止损参考）。
- 实时链路：REST 全量快照 → WS 增量更新最后一根K线（`series.update`）→ 收盘/节流 analysis 推送刷新面板与指标线；断线指数退避重连。

## 4. 实施顺序
1. 后端指标库 + 自带测试全绿。
2. Binance REST 客户端 + 三个 REST 接口（curl 实测）。
3. WS 上游服务 + `/ws` 推送。
4. 前端骨架：顶栏 + 图表（REST 快照渲染）。
5. WS 实时更新 + 分析面板接入。
6. 收尾：加载/错误空态、断线重连、`bun run build`、整体人工验收。

## 5. 验证标准
- 自带测试通过：`dotnet run --project backend/Mdk.Tests` 全部用例绿（Wilder/RSI/ATR/ADX/TradingPair 与已知数值一致）。
- 后端 curl 返回真实币安数据；WS 收到实时 kline 推送。
- 前端本地联调：多周期切换、指标渲染、实时跳动、点位/信号上图。
- 页面截图视觉验收（布局与线框一致）。

## 6. 风险与备注
- 币安接口部分地区需代理：REST BaseUrl 与 WS 域名均可配置。
- 本机 .NET SDK 9/10 已装（无 SDK 8），后端目标框架定为 **net10.0（LTS）**；bun 1.4.0 已装。
- lightweight-charts **v5 与 v4 API 差异大**（panes、markers 改为插件 `createSeriesMarkers`），锁定 ^5 按 v5 写法。
- K线时间轴默认 UTC 显示，后续可加时区切换。
