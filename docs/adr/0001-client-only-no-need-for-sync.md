# 纯客户端 mod，标 NoNeedForSync，主机补丁默认关

用户几乎总是进别人的房间，主机不会装 mod；且装了联网 mod 清单里的 mod 会导致搜不到房或进不去。所以本 mod 默认只改 Remote Client 本地行为，插件类标 `[NetworkCompatibility(CompatibilityLevel.NoNeedForSync)]`，不进联网 mod 清单。

主机侧修复（如 Loader 钩爪滞后补偿）以 Host Patch 形式预留：只在本人当主机（`NetworkServer.active`）时生效，不设总开关，且不新增网络消息。作为客户端时恒为纯客户端。任何需要客户端配合的功能（新增消息等）必须放进另一个标 `EveryoneMustHaveMod` 的独立插件，本插件始终保持 `NoNeedForSync`。

## Considered Options

- 全员安装、改成本地权威移动：消除往返最彻底，但要求所有人装同版本，且伤害权威分叉；被否决。
- 只做主机端：解决不了客户端观感，且用户不是主机；被否决。

## Consequences

- 命中、伤害、敌人被拉等主机权威行为在默认配置下不会改善，只能靠客户端预测、瞄准修正和平滑掩盖。
- 测试要断言启动后联网 mod 清单不含本插件 GUID。
