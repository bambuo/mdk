# MDK 交易分析工作台

基于 **C# (.NET) 后端 + Vue3 前端** 的加密货币行情分析工作台：现货与 U 本位合约双市场、多周期K线（币安公开数据）、Wilder 公式指标体系、趋势方向判定、支撑/阻力位识别与买卖点信号。

详细设计见 [PLAN.md](./PLAN.md)。

## 功能

- **双市场**：顶栏「现货 / 合约」切换，现货走 `api.binance.com`，U 本位永续走 `fapi.binance.com`；各自维护交易对目录与实时流。
- **K线图表**（TradingView Lightweight Charts v5）：5m ~ 1w 多周期切换，主图叠加 EMA20/50/200、布林带通道，RSI 与 MACD 独立副图。
- **趋势方向**（概念一）：EMA200/EMA50 排列 + DMI/ADX 强度打分 → 做多 / 做空 / 观望 + 0–100 置信分 + 中文理由。
- **关键点位**（概念二）：摆动高低点分形识别 + 0.5×ATR 聚类，自动标注支撑（绿）/阻力（红）水平线，强度=触碰次数，右栏点击可在图上定位。
- **买卖点**（概念三）：收盘确认 + 盘中预警两级（去重绘）；EMA 快慢线交叉（1h 用提速参数 10/30）、RSI 30/70 反转、MACD 金叉死叉；同源同向 6 根冷却去重；每信号附 2×ATR 止损参考、多周期共振标记（顺大势/逆大势）。
- **信号绩效闭环**：所有信号自动落库并事后评估（持有 12 根的收益、超额、止损命中、扣费后正负），`GET /api/signal-stats` 与前端信号卡展示**分来源的历史胜率 + 波次口径 + 准入分级**（可参考/仅观察/不达标/样本不足），以及**按市场状态（ADX）与共振状态分组**的绩效（统计默认收起为一行摘要，点击展开为数据表）。后台每 120s 重新分析最近关注的组合，无浏览器连接时也持续积累样本。
- **信号状态标注**：每条信号带市场状态（趋势市/震荡市 ADX、波动率）、共振标记（顺/逆大势，标注"无显著差异"）、确认级别（已收盘确认 / 盘中预警），并提供共振三态筛选与来源筛选。
- **实验性结构信号**：价格触及由已确认历史摆动点构成（≥3 次触碰）的支撑/阻力并有效收回时给出信号，无未来函数；以"结构"来源单独统计，可单独隐藏。
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
| `GET /api/analysis?market=spot\|futures&symbol&interval&limit` | 趋势 + 点位 + 信号 + 指标序列 |
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
    Analysis/    趋势判定 / 支撑阻力 / 信号引擎（纯函数，可测）
    Binance/     REST 客户端（双市场）+ 交易对目录 + K线 WS 上游管理
    Endpoints/   REST + /ws
  Mdk.Tests/     自带测试（dotnet run 即跑，退出码 0 = 全绿）
frontend/
  src/components/  TopToolbar（市场/币种/周期/指标开关）/ ChartPanel / AnalysisPanel
  src/composables/ useKlineSocket（自动重连）
```

## 备注

- K线时间轴按 UTC+8 显示。
- 目录只收录状态为 `TRADING` 的交易对：现货已停牌（`BREAK`）的币种（如改名为 Heima 的 LIT 现货）不会出现；同名合约仍在时可切到「合约」市场查看。
- RSI/ATR/ADX 均为 Wilder 平滑（α=1/N），与 TradingView 的 RMA 一致；与 `span=N` 的 EMA 不是一回事。
- 本工具仅做技术分析展示，不构成投资建议。
