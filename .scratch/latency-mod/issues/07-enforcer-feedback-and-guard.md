# 07 — Enforcer 即时反馈 + 卡住兜底（P3、P5）

**What to build:** Shield Tackle（轻击、重击）按键后立刻有本地动画、音效和特效；载具状态迟迟不来时客户端自己退回正常状态，不再卡在蓄力动作。

**Blocked by:** 02, 03

**Status:** ready-for-agent

- [ ] 在 150ms 与 500ms 延迟下，松键后立即有反馈，不再停顿一个 RTT
- [ ] 载具状态到达时反馈不重复播放
- [ ] 用 clumsy 制造高延迟或丢包，蓄力动作不再卡住；兜底时长为 max(1.5×RTT, 0.3s)，上限 1.5s
- [ ] 记录载具状态与座位消息的实际到达顺序，供任务 08 使用
- [x] 轻击和重击都覆盖；开关关闭后行为回到原版（代码路径层面：ProxyShieldDashHeavy/Light 与 VehicleShieldDashHeavy/Light 均纳入分派；ShouldRun 实时读开关）

> 未勾选项均为需游戏内验证的验收项 → 游戏内验证见 `docs/test-guide.md` §3.5。
