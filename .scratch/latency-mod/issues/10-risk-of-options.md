# 10 — Risk of Options 接入

**What to build:** 所有补丁的开关和强度出现在 Risk of Options 设置界面，没装它时 mod 照常运行，仍可手动改配置文件。

**Blocked by:** 04, 07

**Status:** ready-for-agent

- [ ] 装了 Risk of Options 时，每个补丁的开关和可调强度都能在界面里改并即时生效或提示需重启
- [ ] 卸载 Risk of Options 后游戏启动无报错，补丁仍可用
- [ ] 与已装的 ZioRiskOfOptions 同时存在时无冲突
- [x] 配置文件项带说明文字（`PatchConfig` 每条 `ConfigDescription` 均为中文说明）

> 未勾选项均为需游戏内验证的验收项 → 游戏内验证见 `docs/test-guide.md` §3.8。
