# MDK 交易分析工作台

基于 **C# (.NET) 后端 + Vue3 前端** 的加密货币行情分析工作台：现货与 U 本位合约双市场、多周期K线（币安公开数据）。**核心是缠论分析**（包含处理 → 分型 → 笔 → 中枢 → 背驰 → 1/2/3 类买卖点），EMA/RSI/MACD 与趋势/支撑阻力仅作图表辅助，**不产生信号**。

详细设计见 [PLAN.md](./PLAN.md)。

## 功能

- **双市场**：顶栏「现货 / 合约」切换，现货走 `api.binance.com`，U 本位永续走 `fapi.binance.com`；各自维护交易对目录与实时流。
- **K线图表**（TradingView Lightweight Charts v5）：5m ~ 1w 多周期切换。**以缠论结构为主图层**：中枢以半透明色带填充、笔折线加粗、缠论买卖点用方形 1/2/3 标记；EMA20/50/200、RSI、MACD 作为辅助图层（BOLL 与支撑阻力线默认关闭，可在顶栏开启）。
- **趋势方向**（概念一）：EMA200/EMA50 排列 + DMI/ADX 强度打分 → 做多 / 做空 / 观望 + 0–100 置信分 + 中文理由。
- **关键点位**（概念二）：摆动高低点分形识别 + 0.5×ATR 聚类，自动标注支撑（绿）/阻力（红）水平线，强度=触碰次数，右栏点击可在图上定位。
- **买卖点**（纯缠论）：只有**不可修订的笔**才出信号，记账在结构确认那根K线（无未来函数）；每信号附结构失效位止损参考与级别共振标记。EMA/RSI/MACD 已从信号系统移除，仅作图表指标。
- **历史回填（样本沉淀）**：币安可按区间分页拉取历史K线，服务启动后自动把过去 `Backfill:Days`（默认 90 天）
  的历史**逐根复算**为缠论样本（与在线同口径、无未来函数），41 秒即可写入 1000+ 样本——绩效统计不必再等数月。
  手动触发：`POST /api/backfill/run?days=90&symbols=BTCUSDT,ETHUSDT&intervals=1h,4h&subLevel=true`，进度查 `GET /api/backfill/status`。
  样本带 `origin`（live / backfill）以便区分"在线"与"事后回填"。
- **桌面提醒**：顶栏「提醒」按钮开启后，新出现的**已确认缠论信号**（6 小时内）会弹桌面通知；需浏览器授权。
- **信号绩效闭环**：所有缠论信号自动落库并事后评估（持有 12 根的收益、超额、止损命中、扣费后正负），`GET /api/signal-stats` 与前端信号卡展示**按类别的历史胜率 + 波次口径 + 准入分级 + 分级依据**，以及**按市场状态（ADX）与共振状态分组**的绩效（统计默认收起为一行摘要，点击展开为数据表）。
- **监控页**：用户指定并持久化的监控列表（存 `data/signals.db` 的 `watchlist` 表，标的拆 `base_asset`/`quote_asset`、周期存逗号分隔文本），后台每 `Signal:WatchlistIntervalSeconds`（默认 120s）逐 (市场, 交易对, 周期) 分析，**无浏览器连接也持续运行**。监控页展示各周期结构快照（笔方向/中枢/最近买卖点）与**跨周期共振**（同向 / 分歧 / 无）。加入监控后立即预热一次。
- **准入分级按证据强度**（与周期/类别无关）："可参考"需同时满足 独立波次 ≥30、超额 t≥2、扣费后为正 ≥50%、中位超额 >0、单一标的占比 ≤50%；不达标时给出具体原因（如"超额 t=−0.67 < 2"）。门槛写在统计表底部，避免"样本够但全靠一个标的"被误当有效。
- **级别共振标签**：本级别买卖点前 1 根高周期K线内若出现同向高周期缠论买卖点，标记为「共振」（另有「逆向」与无标签）；
  面板提供"只看共振"筛选与**高周期结构上下文**（高周期笔方向、价格是否在其中枢内、最近买卖点）。
  ⚠ 该标签经 3752 条回填样本检验**未显示统计优势**（波次口径 t=0.53），故**仅作展示，不用作过滤或提醒优先级**。
- **信号状态标注**：每条信号带市场状态（趋势市/震荡市 ADX、波动率）、共振标记（顺/逆大势，标注"无显著差异"）、确认级别（已收盘确认 / 盘中预警），并提供共振三态筛选。
- **缠论结构（笔级别）**：包含处理 → 分型 → 笔（新笔口径，可切老笔）→ 笔中枢 → MACD 面积背驰 → 1/2/3 类买卖点。主图可叠加笔折线、中枢带与买卖点标记（顶栏「缠论」开关，默认关），右栏有"缠论结构"卡片（最新笔方向、中枢区间与笔数、现价是否在中枢内、最近买卖点）。买卖点是唯一的信号来源（落库 `缠论`）；严格无未来函数，且确认时若价格已越过结构失效位则丢弃该信号。
- **缠论结果可复现**：缠论恒定在最近 `Chan:AnalysisBars`（默认 700）根的**固定内部窗口**上计算，与显示窗口无关；起点落在内部窗口前 `Chan:WarmupBars`（默认 30）根内的中枢不产生信号。因此不同 limit 的调用、以及窗口随时间滑动，得到的结构与信号保持一致（实测 300/500/1000 根显示窗口的信号集合完全一致）。
- **多级别结构叠加**：主图同时显示 **高周期（橙，宽区间）/ 本级别（紫，主体）/ 次级别（蓝，细粒度）** 三级中枢色带，
  顶栏「多级别」开关控制；面板给出各级别中枢数与最新区间，本级别中枢标注 `4h内`（位于更大级别震荡区内）与 `+N小`（期间形成的次级别中枢数）。
  次级别数据按显示窗口回溯拉取，覆盖不足时面板会提示"自某时起有数据"。
- **缠论次级别确认**（轻量版）：本级别买卖点还需**次级别（下一档周期）在结构参考点之后 `Chan:SubLevelConfirmWindowBars`（默认 30）根内完成一笔同向笔**（即次级别及时转向）。可用 `Chan:RequireSubLevelConfirm=false` 关闭以做对照。
- **实时更新**：浏览器 ⇄ C# 后端 WebSocket ⇄ 币安流；K线逐秒增量、收盘/20 秒节流刷新分析。

## 运行

依赖：.NET SDK 9/10、bun（或 node ≥ 20）。

```bash
# 1. 自带测试（零外部依赖）
dotnet run --project backend/Mdk.Tests

# 2. 后端（http://localhost:5099）
dotnet run --project backend/Mdk.Api

# 3. 前端（http://localhost:5173，已代理 /api 与 /ws 到 5099）
cd frontend && bun install && bun run dev
```

浏览器打开 <http://localhost:5173>。

## API

| 端点 | 说明 |
|------|------|
| `GET /api/symbols?market=spot\|futures&quote=USDT&limit=50` | 交易对列表（按 24h 成交额排序，含交易币/计价币拆分） |
| `GET /api/klines?market=spot\|futures&symbol=BTCUSDT&interval=1h&limit=500` | K线快照（秒级时间 + OHLCV） |
| `GET /api/analysis?market=spot\|futures&symbol&interval&limit` | 缠论结构 + 买卖点信号 + 指标序列 |
| `GET /api/watchlist` | 监控列表 + 各周期结构快照 + 跨周期共振 |
| `POST /api/watchlist` | 新增/更新监控交易对（body：`{market, symbol, intervals[]}`） |
| `POST /api/watchlist/toggle?market=&symbol=` | 启用/停用监控交易对 |
| `DELETE /api/watchlist?market=&symbol=` | 移除监控交易对 |
| `GET /api/watchlist/signals?limit=50` | 监控列表内的最近缠论信号 |
| `WS /ws?market=spot\|futures&symbol&interval` | `{type:"kline"\|"analysis"}` 实时推送 |

- `market` 省略时默认 `spot`。
- 交易对统一用值对象 `TradingPair` 表示（交易币 `BaseAsset` + 计价币 `QuoteAsset`），拆分以币安 `exchangeInfo` 为权威来源；对外为 `BTCUSDT` 连写格式，展示为 `BTC/USDT`。
- 币安域名可在 `backend/Mdk.Api/appsettings.json` 配置：`Binance:RestBaseUrl` / `WsBaseUrl`（现货）、`Binance:FuturesRestBaseUrl` / `FuturesWsBaseUrl`（合约），受限网络可换镜像。
- 现货 `exchangeInfo` 体积已达 ~17MB，后端启用 gzip/br 压缩传输并设置 60s 超时，勿调回短超时。
- **K线实时链路带 REST 轮询兜底**：上游 WS 静默超过 8 秒时，自动改为每 5 秒轮询币安 REST 最新K线并推送（WS 恢复后自动停用）。部分网络环境下 `fstream.binance.com`（合约 WS）只完成握手不推数据，此机制保证合约行情仍实时。
- **实时性自查**：顶栏右上角显示"实时连接 · 实时 / Ns 前"（超过 15 秒未收到推送变黄）。切换币种/周期时图表不重建，只换数据；图中价格标签应与顶栏价格一致。
- 交易对下拉按成交额排序，但**自动默认标的会跳过锚定币**（USDC/USDT 等价格恒定，图表看似"不动"），优先 BTC。

## 结构

```
backend/
  Mdk.Api/
    Domain/      TradingPair 值对象 / MarketKind / 周期白名单
    Indicators/  Wilder 平滑(RMA) + RSI/ATR/ADX/EMA/SMA/MACD/BOLL（纯 C#）
    Analysis/    缠论（Chan/：分型/笔/中枢/背驰/1·2·3 类买卖点）+ 监控列表与后台监控 + 台账/绩效
    Binance/     REST 客户端（双市场）+ 交易对目录 + K线 WS 上游管理
    Endpoints/   REST + /ws
  Mdk.Tests/     自带测试（dotnet run 即跑，退出码 0 = 全绿）
frontend/
  src/components/  TopToolbar（市场/币种/周期/图层开关）/ ChartPanel / AnalysisPanel / MonitorView（监控页）
  src/composables/ useKlineSocket（自动重连）
```

## 校验

前端静态检查：`cd frontend && bun run build`（= `vue-tsc --noEmit` + 构建）。vue-tsc 需要 `node` 在 PATH 上；
若本机只有 bun，可用便携 node（解压 node-v*-darwin-arm64 到临时目录并临时加入 PATH）运行，否则 vue-tsc 会误报
`Cannot find module './App.vue'`。

`./tools/verify/run-all.sh`（需后端在 :5099 运行）一键跑 7 项独立校验：结构与信号一致性、中枢几何独立复算、
跨显示窗口可复现性、无未来函数（截断对照）、可用性度量、绩效严格检验、绩效稳健性。

绩效类脚本直接读信号台账 `backend/Mdk.Api/data/signals.db`（SQLite，见 `tools/verify/signals-db.cjs`）。
全部脚本用 `bun` 运行（`bun:sqlite` 为内置模块，无需额外依赖）。

## 备注

- K线时间轴按 UTC+8 显示。
- 目录只收录状态为 `TRADING` 的交易对：现货已停牌（`BREAK`）的币种（如改名为 Heima 的 LIT 现货）不会出现；同名合约仍在时可切到「合约」市场查看。
- RSI/ATR/ADX 均为 Wilder 平滑（α=1/N），与 TradingView 的 RMA 一致；与 `span=N` 的 EMA 不是一回事。
- **信号台账是 SQLite**（`backend/Mdk.Api/data/signals.db`，本地积累，不入库）：一行一条信号，标的拆成
  `base_asset` / `quote_asset` 两列，绩效列由后台服务在持有期满后回填；旧的 `signals.jsonl` 已在首次启动时
  导入并保留为备份，此后不再写入。小数在库里存定点文本（SQLite 无 decimal，存 REAL 会引回浮点误差）。
- **信号可信度按证据呈现，不给未校准的"置信度"**：每个信号带经验徽章（同语境波次、胜率与 95% Wilson 区间、
  扣费后为正、中位超额、典型止损距离），波次 <30 时显示"样本不足"。口径见 `Analysis/SignalCredibilityRules.cs`。
- **门控只在通过预注册检验后启用**：`tools/verify/chan-gate-study.cjs` 用"前 60% 选择 / 后 40% 验证 + 波次去重"
  检验候选过滤条件；当前 13 个候选**全部未通过**，因此不启用任何过滤，仅把入场质量（已走 x.xxR）与止损距离作为事实提示。
- **联合打分（结构共振 + 技术指标）已预注册但未通过检验**：`Analysis/JointScoreRules.cs` 等权 6 项
  （结构共振 / 均线同向 / ADX≥25 / 动量一致 / 强背驰 / 入场不追高），信号卡显示"打分 x/6"与逐项命中；
  `tools/verify/chan-score-study.cjs` 前 60%/后 40% + 波次口径检验结果为**高分档样本过少、无单调性 → 未通过**，
  故**仅作事实聚合展示，不启用"可信度 / 可实盘"语义**。详见 PLAN §0.19。
- **锚定币（USDC/USDT 等）不是分析标的**：判定在领域层 `Domain/PeggedAssets.cs`，后端落库/自选/回填/统计与前端默认标的选择共用同一规则。
- **小数一律用 `decimal`**（价格、指标、止损、盈亏），不用 `double`：交易所给的十进制字符串可原样接住，累加比较无二进制漂移。
  JSON 输出统一去掉标度与伪精度（八位小数 + 去尾随零，见 `Domain/DecimalJsonConverter.cs`，仅作用于序列化）；
  指标未定义的位次为 `null`（不再用 `NaN` 哨兵）。
- 本工具仅做技术分析展示，不构成投资建议。
