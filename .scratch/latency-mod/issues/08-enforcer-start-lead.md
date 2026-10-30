# 08 — Enforcer 起点前推（P4）

**What to build:** 松键时把上报给主机的冲刺起点沿冲刺方向前推延迟期间会走到的距离，让主机的载具到达时被拉回的幅度变小。有封顶，遇墙缩短。

**Blocked by:** 07

**Status:** ready-for-agent

- [x] 先验证主机对该起点是否有校验或异常处理；若有，更新规格并停止本票（已按反编译源码核对：`NetworkStateMachine.HandleSetEntityState` 只做归属校验，至 `ActivateVehicle` 全路径无范围校验/拒绝/回滚，故继续本票）
- [x] 前推距离、封顶（0.25 秒的冲刺位移）、遇墙缩短的计算为纯函数并有单元测试——`PredictionMath.StartLeadWithWall`，`PredictionMathTests` 覆盖
- [ ] 在 150ms 与 500ms 延迟下，冲刺开始时被拉回的距离相对关闭时明显减小（记录两组对比）
- [ ] 不出现穿墙或卡进地形；极高延迟时超出封顶的部分不补偿
- [x] 开关关闭后行为回到原版（代码路径层面：ShouldRun 实时读开关，关闭时不改 positionFromAuthority）

> 未勾选项均为需游戏内验证的验收项 → 游戏内验证见 `docs/test-guide.md` §3.6。
