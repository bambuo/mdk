# 校验脚本（verify）

对工作台的后端做**独立校验**——不依赖被测代码自身的断言，而是从接口取真实数据后独立复算/对照。
全部用 `bun` 运行（含 `node:` 风格脚本；本机不需要 node），前置条件：后端已在 `http://localhost:5099` 运行（`dotnet run --project backend/Mdk.Api`）。

| 脚本 | 校验内容 | 运行 |
|---|---|---|
| `chan-consistency.ts` | 缠论结构与信号的基本一致性：笔端点与分型点重合、中枢带 Zg>Zd 且成对、序列长度、摘要与序列一致、买卖点在范围内且止损方向正确 | `bun tools/verify/chan-consistency.ts` |
| `chan-pivot-verify.ts` | **中枢几何独立复算**：由 `chanStroke` 重建笔，验证每个中枢的 `[Zd,Zg]` 确为该中枢"成形三笔"的重叠区间，并检查中枢互不重叠、摘要与现价位置一致 | `bun tools/verify/chan-pivot-verify.ts [SYMBOL] [INTERVAL]` |
| `chan-repro.ts` | **可复现性**：同一内部窗口下，不同显示窗口（300/500/1000 根）在重叠区的缠论信号集合与中枢带取值必须完全一致 | `bun tools/verify/chan-repro.ts` |
| `chan-nolookahead.ts` | **无未来函数**：完整窗口内"截至截断时刻"的缠论信号，必须在截断窗口的结果中同样存在 | `bun tools/verify/chan-nolookahead.ts` |
| `chan-usability.ts` | 可用性度量：信号频率、结构滞后（参考点→记账点）、入场滞后、止损距离、信号时 ADX | `bun tools/verify/chan-usability.ts` |
| `signal-perf-audit.cjs` | 信号绩效严格检验：全部 vs 波次去重、分类别/周期/标的拆解、方向拆分 | `node tools/verify/signal-perf-audit.cjs` |
| `signal-robustness.cjs` | 绩效稳健性：时间聚类（按日聚合）、去集中度（剔除最大标的）、分布与最差情形、近似样本外 | `node tools/verify/signal-robustness.cjs` |
| `chan-gate-study.cjs` | **门控研究（预注册判据）**：13 个候选可交易性门控（入场已走 ≤xR、止损距离、ADX 体制、顺大势、类别、周期及组合）在"前 60% 选择 / 后 40% 验证"两段上分别检验，要求波次 ≥30、t ≥2、中位超额 >0、扣费后为正 ≥50%、集中度 ≤50%；无一通过即不启用任何过滤 | `bun tools/verify/chan-gate-study.cjs` |
| `chan-score-study.cjs` | **联合打分预注册检验**：结构共振+技术指标等权 6 项打分，前 60% 选择 / 后 40% 验证 + 波次口径，检查高分组（≥5）是否达标且优于低分组（单调性） | `bun tools/verify/chan-score-study.cjs` |
| `chan-ab-sublevel.cjs` | A/B 对照：次级别确认开关对缠论回填样本表现的影响 | `node tools/verify/chan-ab-sublevel.cjs` |
| `chan-ab-confluence.cjs` | A/B 对照：级别共振标签（confluence）的表现——判据在跑数据前写定，未达标即结论"证据不足" | `node tools/verify/chan-ab-confluence.cjs` |
| `chan-pivot-scale.cjs` | 笔中枢的尺度特性：各周期中枢宽度（%）是否按 √时间 自相似增长（用于讨论"级别"是否规范） | `node tools/verify/chan-pivot-scale.cjs` |
| `chan-mtf-probe.cjs` | 多周期共振候选规则探测：高周期均线对齐 / 跨级别缠论信号共振 / ADX / 组合，按波次口径给出 t 值 | `node tools/verify/chan-mtf-probe.cjs` |

`./run-all.sh` 一键跑上面 7 项（需后端在 :5099；其余脚本按需单独运行）。

说明：绩效类脚本（`signal-perf-audit.cjs` / `signal-robustness.cjs` / `chan-ab-*.cjs` / `chan-mtf-probe.cjs`）
直接读信号台账 `backend/Mdk.Api/data/signals.db`（SQLite），统一经 `signals-db.cjs` 读取并把行还原成脚本
一直使用的结构（`symbol` 连写、`outcome` 嵌套对象），以保证换存储不改变统计口径；库里标的为
`base_asset` / `quote_asset` 两列，`symbol` 由两者拼出仅供分组使用。脚本用 `bun` 运行。
