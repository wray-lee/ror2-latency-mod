# 09 — Enforcer 退场平滑（P6）

**What to build:** 冲刺结束时，位置在 0.1–0.2 秒内平滑过渡到主机给出的退场位置，消除向前跳 2–3m 的突变。

**Blocked by:** 07

**Status:** ready-for-agent

- [x] 插值为纯函数并有单元测试——`PredictionMath.ClampInterpolationDuration` / `InterpolationAlpha`，`PredictionMathTests` 覆盖
- [ ] 在 150ms 与 500ms 延迟下退场不再出现可见的瞬移
- [ ] 平滑期间的输入和落地行为正常，不出现陷入地形
- [x] 开关关闭后行为回到原版（代码路径层面：ShouldRun 实时读开关；关闭时走原版硬贴）

> 未勾选项均为需游戏内验证的验收项 → 游戏内验证见 `docs/test-guide.md` §3.7。
