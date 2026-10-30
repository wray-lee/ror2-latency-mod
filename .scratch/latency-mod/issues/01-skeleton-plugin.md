# 01 — 空壳插件 + 联网清单不可见

**What to build:** 一个能加载进游戏的 BepInEx 插件，带联网兼容标记 `NoNeedForSync`。装上后游戏能进主菜单，搜房和加房与未装时行为一致，联网 mod 清单里没有本插件。

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] 工程可用 .NET 8 SDK 编译
- [ ] 产物放入测试 profile 后游戏正常启动到主菜单
  - 需用户运行游戏验证，见 docs/manual-checks.md
- [ ] 启动日志输出插件加载信息
  - 需用户运行游戏验证，见 docs/manual-checks.md
- [ ] 运行时断言：联网 mod 清单不含本插件 GUID，且清单哈希与未装本插件时相同（断言失败会明确报错）
  - 需用户运行游戏验证，见 docs/manual-checks.md
- [x] 单元测试项目就绪并能跑通一个示例用例
- [x] 不修改 Default profile 与游戏目录
