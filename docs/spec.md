# 规格：RoR2 客户端延迟缓解 mod

术语见 `CONTEXT.md`；决策见 `docs/adr/0001-*`；根因分析见 `docs/analysis-2026-10-22.md`；测试环境见 `docs/research-test-setup.md`。
Status: ready-for-agent（测试 seam 待用户确认，见 Testing Decisions）

## Problem Statement

作为 Remote Client 进入别人的房间时，延迟在 100–200ms，差的主机接近 1000ms。此时：

- Enforcer 的 Shift（Shield Tackle，轻击/重击）按下后有明显停顿才触发；重击的蓄力动作容易卡住不退出；冲刺结束时位置向前跳 2–3m。
- Loader 的默认钩爪：钩子出现得晚；瞄准移动目标经常钩不中；出钩后钩子显示在目标上，自己却没被拉过去。

原因是这些技能的关键状态都由 Host 生成或判定，Remote Client 要等一个 Round Trip 才能看到结果（分析见 `docs/analysis-2026-10-22.md`）。用户不是 Host，也无法要求 Host 安装 mod。

## Solution

一个纯客户端 mod：在 Remote Client 本地做 Prediction 和 Reconciliation，掩盖 Round Trip，但不改变任何 Authoritative Result（伤害、命中、敌人被拉）。插件对联网 mod 清单不可见，因此不影响搜房和加房。每个修复是一个独立的 Skill Patch，可单独开关，失效时只关闭自己并退回原版行为。框架预留 Host Patch，仅当本人是 Host 时生效，v1 不实现。

## User Stories

1. 作为 Enforcer 玩家，我想在按 Shift 的瞬间就看到并听到反馈，这样我知道技能已经触发而不会无意识地多按。
2. 作为 Enforcer 玩家，我想轻击不再出现明显停顿，这样手感接近低延迟时。
3. 作为 Enforcer 玩家，我想蓄力动作不再卡住，这样我不必再按一次键来解开。
4. 作为 Enforcer 玩家，我想冲刺结束时不再向前跳 2–3m，这样落点可预期。
5. 作为 Enforcer 玩家，我想冲刺的起点被前推到我延迟期间会走到的位置，这样 Host 的载具到达时我被拉回的幅度更小。
6. 作为 Enforcer 玩家，我想前推有上限且遇墙会缩短，这样不会穿墙或出现异常位置。
7. 作为 Enforcer 玩家，我想载具状态迟迟不来时客户端自己退回正常状态，这样高延迟时不会卡在冲刺前摇。
8. 作为 Enforcer 玩家，我想重击与轻击都被覆盖，这样两种玩法都可用。
9. 作为 Loader 玩家，我想按下钩爪时钩子立刻出现，这样不用等一个 RTT 才看到反馈。
10. 作为 Loader 玩家，我想瞄准移动目标时自动补上延迟造成的提前量，这样命中率提高。
11. 作为 Loader 玩家，我想提前量强度可调、可关闭，这样我能控制它是否像瞄准辅助。
12. 作为 Loader 玩家，我想钩子钉住目标后一定能拉动自己，不因"已钉住"消息晚到而被提前收回。
13. 作为玩家，我想每个补丁有独立开关，这样出问题时能单独关掉。
14. 作为玩家，我想游戏更新后方法对不上时只有该补丁关闭并写日志，其余功能和游戏不受影响。
15. 作为玩家，我想在 Risk of Options 里调整开关和强度，没装时也能改配置文件，这样没有额外依赖负担。
16. 作为玩家，我想装了这个 mod 仍能搜到房间并正常加入，这样不会重蹈其他 mod 冲突的覆辙。
17. 作为玩家，我想 Host 不需要安装任何东西，这样我可以进任何人的房间。
18. 作为玩家，我想作为 Remote Client 时伤害和命中仍由 Host 决定，这样不构成作弊。
19. 作为开发者，我想所有补丁共用延迟估计和平滑工具，这样新增技能只需写一个 Skill Patch。
20. 作为开发者，我想在本机用两个实例加人工延迟复现问题，这样不用依赖真实高延迟房间调试。
21. 作为开发者，我想测试结束后清理所有测试环境，这样不污染用户的 Default profile 和游戏目录。
22. 作为开发者，我想当自己是 Host 时可以挂 Host Patch，这样以后能加主机侧修复而不改框架。

## Implementation Decisions

- 插件标 `NoNeedForSync`（见 ADR 0001），硬依赖 R2API Core 仅为该标记；不新增网络消息；Risk of Options 软依赖。
- .NET 8 SDK 编译，目标 netstandard2.1，引用游戏与 BepInEx 程序集，用 HarmonyX 打补丁。
- 框架只抽三块：Skill Patch 基类（名称、生效端 Client/Host、Enabled 配置项、安装失败自禁用）、延迟估计（取客户端平滑 RTT，U=RTT/2）、Prediction/Reconciliation 的插值工具。
- Host Patch 仅在 `NetworkServer.active` 时生效，无总开关；作为 Remote Client 恒为纯客户端。
- P1 LoaderHookPrediction：默认开（用户决定，仅视觉预测）；开启后运行时把 LoaderHook/LoaderYankHook prefab 的 `allowPrediction` 置 1，按下即见钩子。预测钩是纯客户端视觉副本：跳过 `AssignHookReferenceToBodyStateMachine`（不接管 `FireHook.hookInstance`、不把 `ropeEndTransform` 挂到身体），无 authority 故不 stick、不产生绳子拉力/伤害；在真钩 handoff、`FireHook` 退出、或 `max(2×RTT, 0.5s)`（上限 `FeedbackFallbackMaxSeconds`）超时三条路径销毁（`PredictionMath.PredictedHookDrop`）。handoff 时原版先把预测 ghost 移交给真钩并把预测对象的 ghost 置空，随后才销毁预测物体，不会双毁 ghost。真钩接管后命中/拉人/Yank 仍由服务器判。已知：handoff 时 ghost 会从预测位置跳到权威位置（原版预测交接特性），高延迟下可见回跳。
- P2 LoaderAimLead：在 `TrajectoryAimAssist` 之后，按目标速度 × (U + 0.2s + 飞行时间) 修正射线；强度可调，默认开。
- P8 LoaderFlyTimeoutSlack：`FlyState` 的 `duration = maxTravelDistance / velocity`（≈0.222s）从 owner 收到钩子起算，Host 的 stuck 状态晚到时 owner 先超时进 `ReturnState`，表现为"钩子显示命中却不拉人"。给 `duration` 加与 U 相关的余量，可配置，实测校准；可选地给 `BaseGripState` 松键判定加短宽限。根因尚未实测确认。
- P3 EnforcerInstantFeedback：`ProxyShieldDash.OnEnter` 立即播本地动画/音效/特效，幂等，载具状态到达时不重复。
- P5 EnforcerStuckGuard：载具状态未到达时，`max(3×RTT, 1.0s)` 后（上限 `FeedbackFallbackMaxSeconds`，默认 3s）本地退回 Main；RTT 取 `max(游戏上报, 实测中位数)`，避免尖峰/样本滞后导致提前强退。
- P4 EnforcerStartLead：松键时把 `positionFromAuthority` 沿冲刺方向前推 `速度×U`，封顶 0.25s 的冲刺位移，发送前射线检测遇墙缩短。Host 不校验该位置（读到的代码里没有，`HandleSetEntityState` 全路径未验证）。
- P6 EnforcerExitSmoothing：`OnPassengerExitAuthority` 用 0.1–0.2s 插值到退场位置，消除前跳。
- P7 EnforcerGhostDash：已实现，实验项，默认开（用户决定）；依赖 StartLead（关闭/熔断时幽灵不运行）。幽灵模拟「StartLead 的 lead 段 + 完整冲刺曲线」，终点与服务器载具一致；座位到达后 hold、只抑制本机该乘客的座位硬贴，沿冲刺方向追上后 0.1–0.2s 并入；hold 超时但座位在限距内也并入，座位弹出/过远走原版或 ExitSmoothing。关闭需改 cfg/RoO 后重启游戏，已知风险见 `docs/real-room-test.md`。
- 每个补丁目标方法/字段对不上或运行异常，仅关闭自身并写日志。

## Testing Decisions

- 好测试只检查外部行为：给定 RTT、位置、速度等输入，输出的提前量、前推位置、超时时长是否符合规则；不测 Harmony 内部实现。
- **Seam（待用户确认）**：最高 seam 为"预测计算"纯函数层（提前量、前推位置与封顶、超时时长、插值），输入全部注入，用常规单元测试；另有两个很薄的检查：插件类上存在 `NoNeedForSync` 标记、Skill Patch 安装失败时只禁用自身。Unity/游戏内行为无法自动化，用本机双实例加 clumsy 做手动验收，最终在真实高延迟房间验收。（双实例/clumsy 方案已弃用，见下条。）
- ~~本机验收：两个 r2modman profile（主机/客户端），客户端 `connect 127.0.0.1:7777`，测试 profile 装认证跳过 mod；clumsy（`O:\clumsy-0.3`，用户自己运行）lag 设为 RTT/2，测 150ms 与 500ms。~~ 已不需要：改用真实房间 + `Default` profile 测试，见 `docs/real-room-test.md`。
- 联网清单：运行时断言联网 mod 清单不含本插件 GUID。
- 先例：无（全新项目）。

## Out of Scope

- 伤害、命中、敌人被拉等 Authoritative Result 的任何修改。
- alt 钩爪（拉敌人）的延迟。
- 通用网络回滚或全游戏延迟补偿。
- 任何 Host Patch 的实现（框架预留）。
- 需要客户端配合的功能（须另起标 `EveryoneMustHaveMod` 的独立插件）。
- Enforcer 的其他 Utility 变体（用户用到再加）。

## Further Notes

- 待实测的未知：载具状态与 seat 消息到达顺序；预测钩交接；Host 对前推起点是否有异常处理；vehicle 同步频率；首次 stuck 的 SyncVar 延迟；P8 的根因。
- 前推起点可能被装了位置检测 mod 的 Host 视为异常，只能实测。
- 测试结束后清理：第二个 profile、测试专用 mod、临时配置，列清单给用户核对；不碰 Default profile 与游戏目录。
- 未配置 issue tracker，按用户要求以 md 文件形式保存。
