# 04 — Loader 钩爪预测（P1）

**What to build:** Remote Client 按下 Loader 默认钩爪时立刻看到钩子，而不是等一个 Round Trip；预测钩与主机生成的真钩无缝交接。

**Blocked by:** 02, 03

**Status:** ready-for-agent

- [ ] 在 150ms 与 500ms 延迟下，按下后钩子立即出现，不再晚一个 RTT
- [ ] 预测钩与真钩交接时无明显跳变、无残留的重复钩子
- [ ] 主机接受预测编号；连续快速使用不出现钩子泄漏
- [x] 开关关闭后行为回到原版（代码路径层面：ShouldRun 实时读开关，关闭时不翻转 allowPrediction；Run.Awake 仍会还原）
- [ ] 若预测机制在实测中不可用，记录原因并退回仅提示，不留半成品

> 未勾选项均为需游戏内验证的验收项 → 游戏内验证见 `docs/test-guide.md` §3.2。
