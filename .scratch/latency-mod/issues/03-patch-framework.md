# 03 — Skill Patch 框架 + 延迟估计

**What to build:** Skill Patch 基类与延迟估计，让后续补丁只需关心自己的逻辑。作为 Remote Client 时能得到平滑后的单程延迟 U；补丁有独立开关，安装失败只禁用自己并写日志；Host Patch 仅在本人是主机时生效。

**Blocked by:** 01

**Status:** ready-for-agent

- [x] Skill Patch 有名称、生效端（Client/Host）、独立开关配置项
- [x] 人为让一个补丁安装失败（目标方法找不到）时，仅它被禁用并写日志，其他补丁和游戏正常
- [ ] 本机双实例下，日志中的 U 与 clumsy 设置一致（误差合理）；作为主机时不启用延迟估计
  - 未勾选：需双实例实测，见 docs/manual-checks.md
- [x] 预测与插值的纯函数有单元测试（输入注入，覆盖边界与封顶）
- [x] Host Patch 在非主机时不挂 hook（用测试确认），v1 无任何 Host Patch 实现
