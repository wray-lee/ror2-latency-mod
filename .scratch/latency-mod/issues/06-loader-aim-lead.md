# 06 — Loader 瞄准提前量（P2）

**What to build:** 在游戏自带瞄准辅助之后，按目标速度和延迟补一个提前量，提高对移动目标的命中率；强度可调、可关闭。

**Blocked by:** 04

**Status:** ready-for-agent

- [x] 提前量计算为纯函数并有单元测试（目标静止、匀速、延迟为 0、封顶情形）——`PredictionMath.AimLeadCapped`，`PredictionMathTests` 覆盖
- [ ] 在 150ms 与 500ms 延迟下对移动目标，命中率相对关闭时有可感知提升（记录两组对比）
- [x] 强度配置为 0 时与原版一致；默认开启（代码路径层面：strength<=0 直接返回原射线，默认值 1）
- [x] 不修改伤害，也不修改主机的判定（仅 postfix 改本地 ray.direction，不新增网络消息）

> 未勾选项均为需游戏内验证的验收项 → 游戏内验证见 `docs/test-guide.md` §3.4。
