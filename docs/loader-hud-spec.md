# Loader 增强模块规格（取代 LoaderRuler）

目的：用户只在用 LoaderRuler 的延迟修复，其 HUD 全关（嫌 UI 丑）。本模块把它的修复并入本 mod，并重做全部 HUD，用户即可卸载 LoaderRuler。
根据其 README 描述的**行为**重新实现；不拷贝其代码与素材（无许可证文件）。仅 Loader 本体（BodyIndex 判定）生效；观战 Loader 时也显示（沿用其行为）。

## 与延迟补偿的关系

HUD 是显示功能，不属于延迟补偿；作为同一 dll 内独立模块 `LoaderHud*`，与补丁共用 Plugin/PatchConfig/RoO，但开关独立，任一 HUD 异常只熔断自身，绝不影响补丁与游戏。纯客户端，不新增网络消息，不改伤害/命中，Plugin 保持 `NoNeedForSync`。

## 修复类（默认开）

1. **钩爪射程被延迟缩短**：由两个默认开的开关共同实现（见 docs/real-room-test.md）：`Loader` / `LoaderFlyTimeout`（`true`，总开关/客户端 FlyState 截止）与 `Loader` / `LoaderRulerEquivalent`（`true`，等价 LoaderRuler 的超时翻倍）。关闭 `LoaderFlyTimeout` 时该补丁整体不运行。截止公式 `base + max(base, ownExtra)`，其中 `base = maxTravelDistance / desiredForwardSpeed`，`ownExtra = min(FlyTimeoutMargin, FlyTimeoutExtraMaxSeconds)`（默认 0.1s，硬上限 0.15s）；关闭 `LoaderRulerEquivalent` 时退回旧规则（未装 Ruler 为 `base + ownExtra`）。另有 owner 端 `FireHook` 兜底 `LoaderHookFallbackMaxSeconds`（默认 3s）；ReturnState 与“钉住后松手”的兜底从进入 ReturnState/松手时刻重新计时，长按后健康回收不会被 FireHook 总时长提前打断。stuck 是服务器 SyncVar（客户端钩子碰撞被关），强制回 ReturnState 前额外等待单程延迟 U + 50ms 固定步余量（上限 1s）；stuck 已同步时绝不强制（#3）。LoaderRuler 原本是客户端 FlyState 在钩子刚体速度为 0 时每 tick 加 dt/2（客户端钩子 kinematic，速度恒 0 → 实际超时约翻倍）；本实现检测其 `Fix Grapple Range` 开关：开着时统一同一截止、不叠加，显式关闭时尊重关闭态、不翻倍（#6）。
2. **准星射程对齐可用飞行距离（含固定余量）**：`Loader` / `LoaderCrosshair` 默认 `true`，可关。把 `LoaderHookCrosshairController.range` 设为**可用射程** = `maxTravelDistance × 超时倍率 + 钩爪碰撞半径`，而不是 prefab 序列化的 80m。准星读取 LoaderFlyTimeout 的**实际运行状态**（未安装、熔断、运行中关闭均按“未运行”回退到 Ruler/原版口径），超时倍率与截止模型一致：`倍率 = (FlyDeadlineSeconds(base, ownExtra, …) + 50ms 固定余量) / base`，`base = maxTravelDistance / desiredForwardSpeed`，`ownExtra = min(FlyTimeoutMargin, FlyTimeoutExtraMaxSeconds)`；单程 U 不计入，避免准星随网络抖动漂移。LoaderHook 当前 prefab 为 `maxTravelDistance=80m`、`desiredForwardSpeed=360m/s`、`SphereCollider.radius=0.25m`，手算示例：
   - 默认（本 mod `LoaderRulerEquivalent` 开，或 LoaderRuler 的 `Fix Grapple Range` 开）：`base = 80/360 = 0.2222s`，`deadline = 0.2222 + max(0.2222, 0.1) = 0.4444s`，加固定 50ms 后 `0.4944s`，倍率 `2.225`，飞行 `360 × 0.4944 = 178m`，显示 `178 + 0.25 = 178.25m`。
   - 本 mod FlyTimeout 开但翻倍关闭（无 LoaderRuler 且 `LoaderRulerEquivalent` 关，或 LoaderRuler 在场但 `Fix Grapple Range` 显式关）：`deadline = 0.2222 + 0.1 = 0.3222s`，加 50ms 后 `0.3722s`，倍率 `1.675`，飞行 `360 × 0.3722 ≈ 134m`，显示 `134.25m`。
   - 本 mod `LoaderFlyTimeout` 关：本 mod 的 `ownExtra` 与固定 50ms 都不生效，只剩 LoaderRuler 自身的状态——Ruler 关为原版 `80m`、显示 `80.25m`；Ruler 开为 `160.25m`。主机/单人/补丁熔断时同理。
   射程扫描只认 Loader 自己的 `LoaderHook` / `LoaderYankHook` prefab（t8 #7：避免其它 mod 的长钩爪把准心抬高到远超真实射程）；两个 prefab 各按自己的 `desiredForwardSpeed` 计算后取较大值（默认都是 80m/360m/s）。扫描成功缓存 prefab 数据，失败每 5s 重试且只告警一次，扫描失败返回 0 并保留 prefab 原 range，不破坏原准心。固定 50ms 余量计入显示，单程 U 不计入（实际窗口只会更大，准星偏保守）。观战他人 Loader 时保留其 prefab 原 range，不套用本地配置；`LoaderCrosshair` 在运行中关闭会把 range 恢复为捕获到的原值并移除蓄力指示器。默认 2× 是明确的平衡改动：提前松手后钩子在 80–160m 命中仍会走 FlyState 的 `DeductOwnerStock`，可能扩大误耗钩爪库存（t8 #2，仅记录，不改伤害/命中）。蓄力拳（`StraightBracketCrosshair`）补回 `HookIndicatorInRange`；冲刺（sprint）时用普通钩爪准心替换冲刺准心。准心颜色/尺寸沿用游戏原资产，无 mod 配置项。该项是纯本地 HUD 修正，主机/单人同样生效（其余延迟补丁仍在主机上跳过）；启动时关闭该开关则本次会话不安装补丁，需要重启才能开启。

## HUD（每项独立开关、默认关，RoO 里可逐项打开；设置含位置、缩放、颜色/透明度）

1. **钩爪剩余时间**：钩爪存在期间显示 `钩爪剩余` + `X.XXs`（`ProjectileSimple.lifetime - stopwatch` 倒计时；`stopwatch` 是私有字段，反射读取）。剩余比例低于 1/3 时数值由配置色渐变为纯红，1/3 以上用配置色。观战 Loader 亦显示（观战取 `FireHook.hookInstance`，错过复制时在开火状态内按 `ProjectileController.owner` 扫描兜底）。
2. **钩爪长度**：显示 `钩爪长度` + `X.X m`，距离 = `aimOrigin` 到钩点（`transform.position + transform.up * normalOffset`）。
3. **速度文字**：显示 `速度` + `X.X m/s`；字号从配置字号按 0→100 m/s 平滑放大到 1.6 倍（阻尼）。`BaseChargeFist` / `BaseSwingChargedFist` 期间多一行 65% 字号的 `瞄准 X.XX`（速度方向与瞄准方向的点积，-1..1；越小说明瞄偏、损失速度）。
4. **拳速记录**：最近 N 次蓄力拳命中（默认 3，范围 1–10），`拳速` 标签下每行 `角度 : 速度`；角度=点积映射到 0–100 的整数百分比，速度=整数 m/s；最新命中在上。速度越大该行字号越大（1.0–1.6 倍，100 m/s 封顶）；雷霆重击先于该拳首次命中时，整行灰色 `#808080` 且 70% 小字。多目标一次拳击只记一条（首次命中）。N 由 cfg `Loader HUD` / `HUD_PunchRecord_Count` 控制，**不在 RoO**；位置用通用 HUD 偏移。仅本地/主机 authority 事件入账：客户端观战远端 Loader 时不会新增记录。
5. **速度方向图标**：无文字；箭头指向速度在屏幕平面的投影方向，整帧旋转；速度 0→20 m/s 用 smoothstep 渐显，透明度再乘配置透明度；瞄准越接近速度方向颜色越绿（-1 红 / 0 黄 / 1 绿）。默认 32 UI px，随配置缩放。图标资源默认 `Textures/MiscIcons/texBasicArrowRight`（游戏自带），找不到/加载失败时回退内置三线段箭头。“静态色/渐变”（`UseAimGradient`）与图标路径（`IconAddress`）是代码级 `public static` 开关，**不是 RoO/cfg 项**；RoO 只有缩放、颜色、透明度、锚点/偏移（字号与底色对图标无效果；渐变模式下颜色只取 alpha）。
6. **钩爪“能量”**（实验）：仅在钩爪 `ProjectileStickOnImpact.stuck` 时显示，标签 `钩爪能量（实验）`；值 = 动能 `0.5mv²` + 重力势能 `mgh` + 绳张力势能 `mg·绳长` + 重击势能 `m·重击加速度·h`，按 `J` / `kJ` / `MJ` 显示，10 Hz 刷新；地面探针失败或高度 < 0.5m 时按 0.5m 兜底。无额外配置项（位置/字号等走通用项）。

## 视觉设计原则（用户评价原版 UI 丑，要求优化）

- 无法在开发时看到游戏画面，所以第一版走“克制、可调”：用游戏自带字体与 TextMeshPro，单色细描边，不使用大面积底板；数字用等宽对齐；使用与游戏 HUD 协调的暖白/橙金主色，仅在“危险/低”状态变红，“好”状态变绿，其余灰白。
- 可调项（每项 HUD，cfg 段 `Loader HUD`，键 `HUD_<Key>_<项>`）：`Enabled`（默认 `false`）、`AnchorX`/`AnchorY`（默认 0.5/0.5 = HUD 安全区中心，近似准心）、`OffsetX`/`OffsetY`（父容器宽/高比例，非绝对像素）、`Scale`（默认 1）、`Color`（默认暖白 `1, 0.94, 0.85`）、`Opacity`（默认 1）、`FontSize`、`BackgroundOpacity`（默认 0.25，0=无底色）。`<Key>` 为 `GrappleDuration` / `GrappleLength` / `SpeedText` / `PunchRecord` / `VelocityIcon` / `GrappleEnergy`；RoO 分类为 `Loader HUD`，条目名为 `<显示名>：启用/颜色/数值字号/缩放/不透明度/底色不透明度/锚点 X/Y/偏移 X/Y`。**描边（0.1 黑）、字体（HGTextMeshProUGUI + 等宽数字）、字号阻尼/速度曲线、速度图标的静态/渐变与图标路径、拳速记录 N** 不在 RoO（N 只在 cfg；其余为代码常量）。从“全部 HUD 关闭”的启动状态首次启用任一项需重启（模块不装 HUD 钩子）；已有任一项开启时再开其它项即时生效。RoO 对所有 HUD 启用项统一显示重启提示，实际只有“启动时全关”这一种情况必须重启。
- 动画简洁：数值变化用短平滑，不使用闪烁；速度文字的字号变化有阻尼。
- 分辨率/UI 缩放变化、观战切换、死亡、换场景后 HUD 元素必须正确创建/销毁，不残留（原版曾有 bug：Fixed hud elements persisting between deaths and specating change targets）。

## 实现约束

- UI 用 UnityEngine.UI + TextMeshPro，挂到游戏 HUD 的 Canvas（`RoR2.UI.HUD`），随 HUD 生命周期创建销毁。
- 数据来源：Loader 的 CharacterBody/CharacterMotor/ProjectileGrappleController（钩爪剩余时间用 `ProjectileSimple.lifetime` 与反射读取的私有 `stopwatch`；长度用 `aimOrigin` 到钩点距离）、Loader 状态（蓄力拳 BaseChargeFist/BaseSwingChargedFist/GroundSlam 等，以反编译源码为准）。
- 所有 hook 体 try/catch+熔断；高频更新避免分配与反射；可测试的数学（字号映射、颜色插值、点积、能量估计、剩余时间）做成纯函数并走 TDD（红先于绿、期望值独立手算、禁止 tautological）。
- 日志：启动汇总列出每个 HUD 的 enabled/installed 状态；运行期仅在状态变化时打印，节流。

## 验证

- 构建 0 警告 0 错误，测试全绿。
- **未实机验证**（截至 2026-10-25）：6 个 HUD 与 2 项修复只做了反编译核对 + 编译 + 纯函数测试；实际外观、观战切换、死亡/换场景生命周期、图标 sprite 朝向仍需进游戏确认。
- 游戏内视觉无法自动验证：docs/real-room-test.md 有“Loader HUD 逐项检查表”（怎么打开、期望外观、可调参数、如何回传截图/日志）。
