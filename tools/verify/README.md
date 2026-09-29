# 校验脚本（verify）

对工作台的后端做**独立校验**——不依赖被测代码自身的断言，而是从接口取真实数据后独立复算/对照。
全部用 `bun` 运行，前置条件：后端已在 `http://localhost:5099` 运行（`dotnet run --project backend/Mdk.Api`）。

| 脚本 | 校验内容 | 运行 |
|---|---|---|
| `chan-consistency.ts` | 缠论结构与信号的基本一致性：笔端点与分型点重合、中枢带 Zg>Zd 且成对、序列长度、摘要与序列一致、买卖点在范围内且止损方向正确 | `bun tools/verify/chan-consistency.ts` |
| `chan-pivot-verify.ts` | **中枢几何独立复算**：由 `chanStroke` 重建笔，验证每个中枢的 `[Zd,Zg]` 确为该中枢"成形三笔"的重叠区间，并检查中枢互不重叠、摘要与现价位置一致 | `bun tools/verify/chan-pivot-verify.ts [SYMBOL] [INTERVAL]` |
| `chan-repro.ts` | **可复现性**：同一内部窗口下，不同显示窗口（300/500/1000 根）在重叠区的缠论信号集合与中枢带取值必须完全一致 | `bun tools/verify/chan-repro.ts` |
| `chan-nolookahead.ts` | **无未来函数**：完整窗口内"截至截断时刻"的缠论信号，必须在截断窗口的结果中同样存在 | `bun tools/verify/chan-nolookahead.ts` |
| `chan-usability.ts` | 可用性度量：信号频率、结构滞后（参考点→记账点）、入场滞后、止损距离、信号时 ADX | `bun tools/verify/chan-usability.ts` |
| `signal-perf-audit.cjs` | 信号绩效严格检验：全部 vs 波次去重、分类别/周期/标的拆解、方向拆分 | `node tools/verify/signal-perf-audit.cjs` |
| `signal-robustness.cjs` | 绩效稳健性：时间聚类（按日聚合）、去集中度（剔除最大标的）、分布与最差情形、近似样本外 | `node tools/verify/signal-robustness.cjs` |
| `chan-ab-sublevel.cjs` | A/B 对照：次级别确认开关对缠论回填样本表现的影响 | `node tools/verify/chan-ab-sublevel.cjs` |
| `chan-pivot-scale.cjs` | 笔中枢的尺度特性：各周期中枢宽度（%）是否按 √时间 自相似增长（用于讨论"级别"是否规范） | `node tools/verify/chan-pivot-scale.cjs` |
| `chan-mtf-probe.cjs` | 多周期共振候选规则探测：高周期均线对齐 / 跨级别缠论信号共振 / ADX / 组合，按波次口径给出 t 值 | `node tools/verify/chan-mtf-probe.cjs` |

说明：`signal-perf-audit.cjs` / `signal-robustness.cjs` 直接读 `backend/Mdk.Api/data/signals.jsonl`（信号绩效日志）。
