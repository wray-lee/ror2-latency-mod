# 复核建议逐条裁决（2026-10-25）

范围：上一轮复核 t8（`v2:9a869c6…feedf8cae`，代码/状态机）与 t9（`v2:d520a5e…770df4d`，HUD）提出、本轮负责收口的建议，以及本轮对抗复核 t5 的返工项。基线 `857b07b`，收口 HEAD `db6fa47`。判定规则：正确性/崩溃/残留/性能问题=修；纯外观/布局偏好=不修并记录；受“不新增网络消息、不改伤害/命中”约束无法修=记录限制。

## t8 复核（代码/状态机）

| 编号 | 主题 | 判定 | 理由 | 落点 / 记录 |
|---|---|---|---|---|
| #2 | 准心 80.25m 与默认 2× 实际飞行（约 160m）矛盾 | 修（显示口径） | t2：准心改为“真实可用射程”= `maxTravelDistance × 超时倍率 + 碰撞半径`，与 FlyTimeout 截止模型同源；提前松手后 80–160m 命中仍扣库存属 2× 平衡改动，按约束不改伤害/命中，只在文档记录为已知风险 | `PredictionMath.HookRangeMeters` / `HookTimeoutMultiplier`；t6 将固定 50ms 同步余量计入后默认 178.25m、翻倍关闭 134.25m；`docs/loader-hud-spec.md:12-17`、`docs/real-room-test.md:145,147`；commit `1af8ef0`、`db6fa47` |
| #3 | “stuck 是本地碰撞检测、不需要 U”与反编译不符，高延迟下强制 ReturnState 可能抢在 stuck 同步前取消真实命中 | 修 | t1：stuck 是服务器 SyncVar（`ProjectileStickOnImpact.cs:63,96`；客户端 `CanProcessCollisionEvents=false`）。FlyState 截止增加单程 U + 50ms、上限 1s 的同步窗口；stuck 已同步时绝不强制返回 | `PredictionMath.StuckSyncGraceSeconds`、`FlyDeadlineWithStuckGrace`；`LoaderFlyTimeoutPatch.cs` 截止判定；测试 `PredictionMathTests.cs`；commit `63ea877` |
| #4 | owner 兜底用 `FindByCustomName(hook,"Hook")` 找错对象，ReturnState 保护是死代码（任务书未列，随 #3/#6 同批修） | 修 | t1：反编译确认投掷物自带 `EntityStateMachine`（`ProjectileGrappleController.cs:9`），`"Hook"` ESM 在玩家身体上。改取 `hook.GetComponent<EntityStateMachine>()`，删除旧查找路径 | `LoaderFlyTimeoutPatch.cs:356-360`；commit `63ea877` |
| #5 | 配置语义/描述与实现不一致；中途变更语义矛盾 | 修 | t3：`extra=max(base, ownExtra)` 可超过原“硬上限”，已把该值更名为“输入上限”并在描述中说明真实语义；修正 HUD/RoO/8 个补丁的 `restartRequired`、GhostDash 假“关后重启生效”及“全部即时生效”日志；LoaderCrosshair 中途关闭残留由 t6 补修 | `PatchConfig.cs`、`RoOIntegration.cs`、`LabelTable.cs`；`LoaderCrosshairPatch.cs:81-91,130-178`；commit `2d9ae90`、`db6fa47` |
| #6 | LoaderRuler 显式关闭 Fix Grapple Range 时本 mod 仍默认翻倍 | 修 | t1：尊重 Ruler 的显式关闭态，不再无条件叠加翻倍；Ruler 开启时统一截止、不双叠加 | `LoaderFlyTimeoutPatch.cs`（Ruler 检测/倍率）；`docs/loader-hud-spec.md:12`；commit `63ea877` |
| #7 | `FullRangeMeters` 扫描所有 FireHook 子类（含其它 mod），异常路径每帧重试 | 修 | t2：扫描限定 `LoaderHook`/`LoaderYankHook`（prefab 名 + FireHook 族 owner state）；成功缓存 prefab 数据；catalog 未就绪 1s 重试且不缓存 0；异常 5s 重试、只告警一次；失败返回 0，调用方保留 prefab 原 range | `LoaderCrosshairPatch.cs:331,419`（t2 行号）；commit `1af8ef0` |

同轮其余项：#1（主机/单人准心静默失效）已在 `857b07b` 修复（`LoaderCrosshairPatch.RequiresRemoteClient=false`）；#8（与 LoaderRuler Improve Crosshair 重叠）与 #9（FlyState 强制返回 vs 服务器销毁竞态）判为可接受，无崩溃/卡死路径，保留观察。

## t9 复核（HUD）

t9 的 4 项必须修复（M1 观战速度按渲染帧、M2 高频路径分配/反射/扫描、M3 拳速钩子缺熔断、M4 危险色不符规格）已在 `857b07b` 处理；下表是本轮 t4 对 S1–S12 的逐条裁决。

| 编号 | 主题 | 判定 | 理由 | 落点 / 记录 |
|---|---|---|---|---|
| S1 | 拳速 OnEnter 回调缺 authority/本地 body 守卫 | 修 | 代理 body 会写入静态 Tracker；三处回调统一在入账前检查 `body.hasAuthority` | `LoaderHudPunchRecord.cs:500,536,559` |
| S2 | 隐藏/暂停/切观战后运动样本未重置，首帧速度尖峰 | 修 | 基类在关闭/不可见时调用 `OnHidden()`，速度文字/图标/能量重置采样 | `LoaderHudElement.cs:89,96,107`；`LoaderHudSpeedText.cs:188`；`LoaderHudVelocityIcon.cs:146`；`LoaderHudGrappleEnergy.cs:28` |
| S3 | 钩爪探针优先返回缓存旧钩 | 修 | 改为当前 Hook ESM 处于 FireHook 时优先 `hookInstance`；缓存仅作同 owner 兜底，非开火态失效 | `LoaderHudGrappleDuration.cs:150-176` |
| S4 | 静态 PunchRecord 钩子/Tracker 残留，全关后非零开销 | 修 | 改实例引用计数；最后一个拳速 HUD 销毁时退订事件、`Harmony.UnpatchSelf()`、清 Tracker；宿主在时保留钩子供运行期开关 | `LoaderHudPunchRecord.cs:249,263-303` |
| S5 | 蓄力拳检测只取第一个 EntityStateMachine，“瞄准”可能永不显示 | 修 | 缓存 `GetComponents<EntityStateMachine>()` 后逐个检查（Loader 另有 Hook 状态机，顺序不保证） | `LoaderHudSpeedText.cs:74,165,195-212` |
| S6 | RoO HUD 开关 `restartRequired` 与无效图标选项 | 修 | `restartRequired` 按启动时是否已有任一 HUD 开启动态决定；速度图标不注册无效的字号/底色滑条 | `RoOIntegration.cs:215,226,235-249` |
| S7 | 默认 Duration/Length 布局可能重叠（OffsetY 0.100 vs 0.065） | 不修（记录） | 纯视觉/布局偏好，所有 HUD 默认关且锚点/偏移可调；无实机截图，改默认值属猜测 | `PatchConfig.cs:196-199`（保持原样） |
| S8 | 能量值缓存失效 | 不修（已修复） | 现码在钩爪对象变化或未粘住时清空 `valueValid/refreshCountdown/motion`；body/钩爪切换必导致对象变化，无同钩换目标路径（t10 已处理） | `LoaderHudGrappleEnergy.cs:44-57` |
| S9 | 观战远端 `aimDirection` 无网络复制 | 不修（限制，仅注释） | 新增网络消息被约束禁止，客户端无法修复；两处标注为近似值 | `LoaderHudSpeedText.cs:220`；`LoaderHudVelocityIcon.cs:102` |
| S10 | 图标首次显示同步加载可能卡一帧 | 修 | Awake 预加载并静态缓存；失败允许首次可见时重试一次，避免永久退化为内置箭头 | `LoaderHudVelocityIcon.cs:57-61,264-270` |
| S11 | 拳速记录 64 条全局上限 | 不修（记录） | S1 守卫后仅本地 authority body（分屏最多两具）写入，Snapshot 已按 body 取最近 N；淘汰只在另一 body 大量命中时发生，边界低风险 | `LoaderHudPunchRecord.cs`（上限逻辑保持原样） |
| S12 | `Host.Update` 在 try 外判断 `AnyEnabled`，异常会每帧重复 | 修 | 把 `AnyEnabled` 判断移入 try；异常时熔断一次并停止，不再每帧刷异常 | `LoaderHudModule.cs:230-249` |

## t5 对抗复核 → t6 返工

| 编号 | 主题 | 判定 | 理由 | 落点 / 记录 |
|---|---|---|---|---|
| P1 | owner 兜底会在健康的 ReturnState/松手返回途中提前结束 FireHook（并导致 ReturnState 每 tick 重入/msg48 抖动） | 修 | ReturnState 与钉住松手各自从进入/松手时刻重算完整兜底窗口；长按 1.5s 后松手不再被总时长提前打断 | `PredictionMath.ReturnFallbackWithinGrace:235`；`LoaderFlyTimeoutPatch.cs:98-100,354-358,381,392,404-408,440-443`；测试 `PredictionMathTests.cs:253`；commit `db6fa47` |
| P2 | 准星显示不是真实强制返回窗口；文档 170m 验收线误导 | 修 | 超时倍率计入固定 50ms 同步余量、仍不计 U；默认 178.25m，翻倍关闭 134.25m；文档改为 170m 可能亮、180m 以上不应亮并注明偏保守 | `PredictionMath.cs:124`；`LoaderCrosshairPatch.cs:393,406`；`docs/loader-hud-spec.md:14-16`、`docs/real-room-test.md:145,147`；测试 `PredictionMathTests.cs:453` |
| P3 | 准星读取原始配置而非 FlyTimeout 实际运行状态 | 修 | 暴露 `RuntimeActive`（installed/开关/熔断/主机跳过）；未运行即回退 Ruler/原版口径 | `LoaderFlyTimeoutPatch.cs:64`；`LoaderCrosshairPatch.cs:393`；测试 `SkillPatchRuntimeTests.cs:84` |
| P3 | 准星热关闭后 range/蓄力指示器残留 | 修 | prefix 捕获原 range（`ConditionalWeakTable`），关闭/熔断/非本地目标时恢复；关闭时移除蓄力指示器 | `LoaderCrosshairPatch.cs:81-91,130-178,239-263,343` |
| P3 | 观战他人 Loader 时套用本地配置 | 修 | 准星与蓄力指示器增加 `targetCharacterBody.hasAuthority` 判断，观战保留 prefab 原值 | `LoaderCrosshairPatch.cs:149,273` |
| P3 | 字体探测缓存不随语言/字体失效，样本仅汉字 | 修 | 缓存按 `defaultLanguageFont` 实例失效；样本改为全部非 ASCII 字符（含全角标点） | `LabelLanguageProbe.cs:18,50-58,77-81`；测试 `LabelTableTests.cs:113` |
| P3 | RoO HUD 重启角标提示不完整 | 不修（记录） | 属提示/外观类，非正确性/崩溃/残留/性能；“统一 restartRequired”会对常规单项热切换误报，保留空宿主会放弃全关零开销路径；`hud.desc.enabled` 已明确警告 | `RoOIntegration.cs:215,226`；`LoaderHudModule.cs:237,248`；`LabelTable.cs:56-59` |

## 统计与遗留

- 裁决：修 20 项（t8 #2/#3/#4/#5/#6/#7 共 6 项；t9 S1–S6/S10/S12 共 8 项；t5/t6 P1/P2 与 P3 中的运行时状态/热关闭残留/观战/字体 4 项，共 6 项）；不修记录 5 项（t8 #8/#9、t9 S7/S11、t5 RoO 角标）；另有 S8 由 t10 已修、S9 仅加限制注释。
- 所有“修”项均已有单元/状态矩阵测试且本地提交，无 dll/二进制入库；详见 `docs/real-room-test.md` 第 9 节实机检查表。
- 未覆盖/需实机确认：ReturnState 全程无 msg48 抖动、170m 可亮/180m 不亮、观战不套用本地 178.25、热关闭后 range/指示器恢复、中文全角标点无 tofu、2× 射程的平衡观感。
