#!/usr/bin/env bash
# 一键运行全部校验（需要后端在 :5099 运行）
set -u
cd "$(dirname "$0")"
fail=0
run() { echo; echo "=== $1 ==="; shift; "$@" || fail=1; }
run "结构与信号一致性" bun chan-consistency.ts
run "中枢几何独立复算" bun chan-pivot-verify.ts
run "跨显示窗口可复现性" bun chan-repro.ts
run "无未来函数（截断对照）" bun chan-nolookahead.ts
run "线段正确性核对" node chan-segment-audit.mjs
run "可用性度量" bun chan-usability.ts
run "绩效严格检验" node signal-perf-audit.cjs
run "绩效稳健性" node signal-robustness.cjs
echo
[ $fail -eq 0 ] && echo "全部校验完成（无断言失败）" || echo "存在校验失败，请查看上方输出"
exit $fail
