# 真实房间自测（Default profile，Remote Client）

适用：`RoR2LatencyMod` 0.1.0；你以 Remote Client 进别人的房间，不搭本机双实例、不用延迟模拟器。主机不需要装本 mod（`NoNeedForSync`），只在你自己的客户端装。除纯本地 UI 的 `LoaderCrosshair`（主机/单人同样生效）外，其余补丁只在**非主机**生效；当 Host 时自动跳过，无副作用。

## 1. 开关入口与默认状态

- 游戏内：`Settings > Mod Options > RoR2 Latency Mod`（需装 Risk of Options；没装则只改 cfg）。
- 配置文件：r2modman 里对 `Default` profile 点 `Browse profile folder`（打开 profile 文件夹）→ `BepInEx\config\com.wray.ror2latency.cfg`。
- 日志：`Default\BepInEx\LogOutput.log`（BepInEx 控制台也可实时看）。

| 补丁 | RoO 名称 | cfg 段 / 键 | 默认 |
|---|---|---|---|
| `LoaderHookPrediction` | 钩爪本地预测 | `Loader` / `LoaderHookPrediction` | **开** |
| `LoaderFlyTimeout` | 钩爪飞行超时补偿 | `Loader` / `LoaderFlyTimeout` | **开** |
| `LoaderRulerEquivalent` | 提供 LoaderRuler 等价补偿 | `Loader` / `LoaderRulerEquivalent` | **开** |
| `LoaderCrosshair` | 钩爪准心修正 | `Loader` / `LoaderCrosshair` | **开** |
| `LoaderAimLead` | 钩爪提前量 | `Loader` / `LoaderAimLead` | **开** |
| `EnforcerFeedback` | Enforcer 冲刺即时反馈 | `Enforcer` / `EnforcerFeedback` | **开** |
| `EnforcerStartLead` | Enforcer 起点前推 | `Enforcer` / `EnforcerStartLead` | **开** |
| `EnforcerExitSmoothing` | Enforcer 退场平滑 | `Enforcer` / `EnforcerExitSmoothing` | **开** |
| `EnforcerGhostDash` | 幽灵冲刺（实验） | `Enforcer` / `EnforcerGhostDash` | **开** |

辅助值默认：提前量强度 `1.00`；提前量上限 `0.15s` 目标行程（`Loader` / `AimLeadMaxSeconds`，0–0.5，0=不补）；飞行超时自身余量 `0.10s`（`Loader` / `FlyTimeoutMargin`，0–4）；该余量硬上限 `0.15s`（`Loader` / `FlyTimeoutExtraMaxSeconds`，0–4）；钩爪 owner 兜底上限 `3.0s`（`Loader` / `LoaderHookFallbackMaxSeconds`，0.5–10，超时按 `max(3×RTT,1s)` 再封顶）；兜底超时 `0s`（按 `max(3×RTT, 1.0s)` 自动，上限见下）；兜底/幽灵/交接 hold 共享上限 `3.0s`（`Enforcer` / `FeedbackFallbackMaxSeconds`，0.5–10）；无实测样本时单向延迟 U 上限 `0.5s`（`Enforcer` / `InitialLatencyCapSeconds`，0.05–2）；起点前推上限 `0.25s`；起点前推距离上限 `0m`（不额外封顶）；退场平滑时长 `0.15s`（限制 0.1–0.2s）。以上补丁开关均已暴露到 RoO（HUD 的拳速记录条数 `HUD_PunchRecord_Count` 例外，只在 cfg）。

延迟取值规则：
- **位置/前推用的 U**：Enforcer 三个补丁优先用 `min(游戏上报 RTT, 实测往返)`；实测往返 = 本机松键进入 Proxy → 载具座位到达的时间差，取最近 5 个样本的中位数（拒绝 <0.05s 或 >8s 的样本，按 FIFO 匹配连续点按）。还没有样本时用游戏上报 RTT，但 U 先按 `InitialLatencyCapSeconds` 封顶。
- **兜底/幽灵超时用的 RTT（本轮修正）**：改用 `max(游戏上报 RTT, 实测中位数)`，避免尖峰时游戏读数偏低/样本滞后导致提前强退；再套 `max(3×RTT,1.0s)`、上限 `FeedbackFallbackMaxSeconds`。低 RTT 下不再 0.4s 就放弃（旧版日志里载具 9ms 后才到）。
- **交接 hold 超时**：`max(2×RTT, 0.5s)`，同样受 `FeedbackFallbackMaxSeconds` 上限约束；正常情况下座位只保留约 0.2–0.35s，超时前就会被弹出并交给 `EnforcerExitSmoothing`。
- Loader 补丁仍用游戏上报值，不受影响。

## 2. 建议开启顺序（低风险 → 高风险）

Default 9 个补丁全部默认开（`LoaderHookPrediction` 为仅视觉预测，用户决定默认开；P7 按用户决定默认开；含 LoaderRuler 等价补偿与准心修正）。第一次进真实房间时按下面逐档确认，出问题就退回上一档。

1. **即时反馈**：`EnforcerFeedback`。只改本地动画/音效和“等不到载具状态就退出”的兜底，不动位置、不新增网络消息；坏了最多表现不对。
2. **本地呈现**：`EnforcerExitSmoothing`、`LoaderFlyTimeout`。前者只在本机插值退场位置（>10m 或异常时直接放弃），后者只延长状态超时；都不改方向/命中。
3. **预测类**：`LoaderHookPrediction`、`LoaderAimLead`、`EnforcerStartLead`。会提前生成钩子、改瞄准方向、把前推后的起点发给主机；错了表现为钩子位置/命中/落点偏差。
4. **位移模拟**：`EnforcerGhostDash`（P7，默认开）。本地模拟「起点前推（0.25s 封顶）+ 完整冲刺曲线」再与载具交接，改动最大，风险最高。手感不对就在 RoO 取消勾选或 cfg 改 `false` 后**重启**；关闭时启动不安装 hook。注意：cfg 里已保存的 `true` 不会因代码默认值改变而变，必须手动改。

## 3. 逐项触发与期望

- **LoaderHookPrediction**：默认开（用户决定；仅视觉预测）。触发—— Loader 用钩爪（Grapple，默认 M2）或副技能变体 Yank Hook 朝墙/远处目标出钩。期望——按下瞬间钩子已飞出；预测钩只做本地视觉：跳过 `AssignHookReferenceToBodyStateMachine`，不接管 `FireHook.hookInstance`、不把绳子挂到身体、无 authority 故不 stick/不拉人，动画不会再被预测钩卡住。真钩 handoff 后由原版接管（命中/伤害仍由主机判；Yank 拉怪仍由服务器执行），预测物体随即销毁（日志 `dropped predicted ... (Handoff) ghostAlreadyTransferred=True`）；`FireHook` 退出或超时（`max(2×RTT,0.5s)`，上限 `FeedbackFallbackMaxSeconds`）也会销毁残留预测钩。两个 prefab 行为一致，入口/销毁日志打印实际 prefab 名称。对比原版——钩子晚一个 RTT 才出现；已知 handoff 时 ghost 会从预测位置跳回权威位置（原版预测交接特性，高延迟下可见回跳），故仍为实验项。
- **LoaderFlyTimeout**：触发——高延迟房里朝远处墙面/悬崖出钩（未钉住），以及钉住/松键后钩子迟迟不销毁。期望——未钉住时 FlyState 在 `base + max(base, 自身余量)` 到期（`LoaderRulerEquivalent` 默认开，约 2×base；关掉且未装 LoaderRuler 时为 `base + 余量`，默认约 base+0.1s；装了 LoaderRuler 且其 Fix Grapple Range 开时统一同一截止，不重复叠加；其 Fix Grapple Range 显式关闭时尊重关闭态，退回 `base + 余量`）；强制回 ReturnState 前再等 `U + 50ms`（上限 1s）让服务器 stuck SyncVar 到达，stuck 已同步时绝不强制（#3）。owner 侧若超过 `max(3×RTT,1s)`（默认上限 3s）仍没拿到 hookInstance、已 Return 未销毁、或松键后仍未返回，则本地退回 Main，玩家能再次出钩。对比原版——高延迟下钩子/技能状态可能长时间滞留，原版没有 owner 侧兜底。
- **LoaderAimLead**：触发——对着横移的敌人（小怪/Boss 均可）出钩，目标在钩爪射程内。期望——只沿原版修正后的射线找“准星上”的目标（≤10°、HurtBox 中心距射线 ≤1m）；速度估计需最后 3 个 `CharacterNetworkTransform` 快照的两次差分一致（跨度 ≥0.12s、速度 ≥1.5m/s、方向点积 ≥0.8、速度比 ≥0.5），按置信度缩放；提前量 = 目标速度 ×（U + 该目标实际插值延迟 + 飞行时间），上限 `AimLeadMaxSeconds=0.15s` 目标行程，垂直分量 ≤水平速度的 35%。目标静止、速度不稳、找不到目标、目标与准星之间隔着世界碰撞时不补。对比原版——按敌人插值旧位置瞄，高延迟下钩子落空；旧版 mod 的 37° 候选锥可能把钩子拉向准星外的其他敌人。
- **EnforcerFeedback**：触发——Enforcer 按 Shift 轻击（点按），以及按住 Shift 蓄力后松手重击。期望——松键进入冲刺的当帧就有动作/音效/特效（蓄力判定本身在本机，按下即正常）；松手后不会卡在冲刺动作里；载具状态一直不来时自动回 Main（本轮改为 `max(3×超时RTT, 1.0s)`，上限 `FeedbackFallbackMaxSeconds=3.0s`；超时RTT = `max(游戏上报, 实测中位数)`）。任何“早期反馈已播、但没走原版 VehicleShieldDashExit 就离开 Proxy”的路径（兜底回 Main、被打断/死亡、开关中途关闭、熔断）会本地补播 `FullBody, Override` 层的 `ShieldTackleExit`；原版稍后播放的退场动画不会被阻止，重复播同一动画无副作用。对比原版——反馈晚一个 RTT；重击蓄力可能卡住，要再按一次才能解。旧版日志里兜底 0.55s 强退（进入时 timeout 1.50s，中途按掉到 0.359s 的 RTT 重算成 0.54s），9ms 后载具才到；本轮进入时缓存超时值，应消失。
- **EnforcerStartLead**：触发——重击（蓄力后松手）最明显，轻击也有；松手瞬间最好还在向前移动。期望——发给主机的起点沿冲刺方向前推 `速度×U`，最多 0.25s 位移；前方 0.5m 内有墙会缩短；载具到达时回拉变小。对比原版——起点就是松手位置，载具到达时被拉回。
- **EnforcerExitSmoothing**：触发——任意 Shift 冲刺结束（轻/重）。期望——退场位置用 0.1–0.2s 插值过去，不再最后一下前跳 2–3m；距离 >10m 视为传送/重生，直接硬贴；受击/死亡/换载具/被外部移动会中止。对比原版——客户端直接硬贴主机退场位置，表现为前跳。
- **EnforcerGhostDash（P7）**：触发——轻击或蓄力重击的松手瞬间（开启并重启后）。期望——松手后本机立即模拟「起点前推 + 完整冲刺曲线」（lead 最多 0.25s 位移、遇墙缩短；冲刺段按载具 `forwardVelocityCurve` 的 0.2s），幽灵终点与服务器载具终点一致；座位到达时保持原地并抑制本机乘客的 `VehicleSeat.UpdatePassengerPosition` 硬贴，等座位沿冲刺方向追上（容差：沿向 2m、侧向 2.5m）后用 0.1–0.2s 并入；正常应看到 `handoff begin` → `caught up ... blend` → `handoff done`（或座位先弹出时 `seat lost ... (ejection)`，交给 ExitSmoothing）。StartLead 与幽灵共用同一个 lead（日志 `ghost=... lead recorded`），不再“幽灵前移 + 起点又前推”双重补偿，也不会再出现“冲一次→瞬移回起点→再冲一次”。**P7 依赖 StartLead 开启**：StartLead 关闭/熔断时幽灵在第一步停止（日志 `StartLead did not report a start`），否则服务器载具仍从 S0 开始，座位会把服务器玩家拉回。对比原版——松手后原地等 RTT，再被突然拉到载具位置。注意——本轮交接逻辑尚未实机验证；高 RTT（>0.5s）时 lead 封顶 0.25s，幽灵显示的位移仍可能比服务器终点短，靠交接 blend 补上（平滑但可能像第二段滑行）；服务器冲刺被打断时幽灵可能超前，退场由 ExitSmoothing 兜底（差 >10m 会硬贴）。另：开启时幽灵位移可能随既有 `CharacterNetworkTransform` 同步给主机/他人（不是新增消息）。

## 4. 需要回传的日志

源码里的 Tag 不带方括号，实际行形如 `EnforcerFeedback: ...`；Loader 补丁用 `LoaderHookPrediction:` 等前缀。用文本编辑器直接搜补丁名即可。

启动先确认：

- `RoR2 Latency Mod 0.1.0 (com.wray.ror2latency) loaded.`
- `[RoR2LatencyMod] patches: LoaderHookPrediction=installed+enabled, ..., EnforcerGhostDash=disabled-by-config`
- 有补丁异常时会有 `Skill Patch '<Name>' failed to install; disabled: ...` 或 `Skill Patch '<Name>' tripped; falling back to vanilla: ...`。

按补丁 grep：

- `LoaderHookPrediction:` → `installed; allowPrediction is enabled on the first client shot ...`；`resolved hook prefabs: LoaderHook@<N>, LoaderYankHook@<M>`；`prefab '<name>' is prediction-capable (localPlayerAuthority=true, original allowPrediction=<bool>)`；`allowPrediction=true on '<name>' (was <bool>); predicted hook enabled`；每次出钩 `FireHook enter prefab='<实际prefab名>' ours=<yes|no> allowPrediction=<bool> original=<bool>`；关闭/新一局 `restored allowPrediction=...`。
- `LoaderFlyTimeout:` → `installed (Ruler-aware FlyState deadline + owner-side FireHook fallback)`；`FlyState enter '<prefab>' predicted=<bool> authority=<bool> base=<s>s ownExtra=<s>s deadline=<s>s rulerFix=<on|off> interp=<ms>ms U=<ms>ms`；`FlyState deadline reached after <s>s (... rulerFix=...) -> ReturnState`；`FireHook got hookInstance after <s>s`；`hook state <旧> -> <新> after <s>s`；`hook destroyed after <s>s`；`owner fallback -> Main after <s>s (timeout <s>s, reason=never got hookInstance|ReturnState not destroyed|not stuck and not returning|released while stuck, no return, ...)`；`FireHook exit after <s>s (hookSeen=..., hookGone=..., forced=..., lastHookState=...)`；`LoaderRuler detected; 'Fix Grapple Range'=<on|off>`。
- `LoaderAimLead:` → `installed (aim lead after vanilla TrajectoryAimAssist)`；`target=<name> dist=<m>m speed=<new>/<prev>m/s dot=<x> span=<ms>ms conf=<0-1> vert=<clamped> (raw <y>, horiz <h>) U=<ms>ms interp=<ms>ms flight=<ms>ms lead=<m>m raw=<m>m cap=<s>s capped=<yes|no> strength=<x> vanillaAdj=<deg> vanillaToFinal=<deg> rawToFinal=<deg> guard=<ok|rejected>`；`no target within 1.0m of ray, vanilla aim`；`velocity not trusted (...), vanilla aim`；`AimLeadStrength=0` / `AimLeadMaxSeconds=0`。
- `EnforcerFeedback:` → `installed (early local feedback, stuck fallback, arrival-order logging)`；`early local feedback on enter (ProxyShieldDashLight|ProxyShieldDashHeavy) U=<s>s gameRTT=<s>s measuredRTT=<s>s|none samples=<N> used=<game|measured>`；`arrival #<N> ... U=<s>s gameRTT=... measuredRTT=... [measuredSample=<s>s median=<s>s n=<N>]`；`stuck fallback -> Main after <s>s (timeout <s>s, cached=<s>s, timeoutRTT=<s>s, U=... gameRTT=... measuredRTT=...) ... reason=vehicle state never observed`（timeout 随当前 RTT 尖峰增长、不缩小；固定 FeedbackFallbackTimeout 覆盖时不变）；`animation reset FullBody, Override=ShieldTackleExit reason=<...>; before=<...>`；`vanilla exit animation seen ... local ShieldTackleExit reset will not run again`；`late=local bodyState=<state>`（载具在 fallback 之后才到，原版座位/退场接管）；`early feedback already played; vanilla OnVehicleEnteredState suppressed when VehicleShieldDashHeavy|VehicleShieldDashLight arrived ...`。
- `EnforcerStartLead:` → `installed (lead applied in NetworkStateMachine.SendSetEntityState, once per msg48)`；普通路径 `lead=<m>m speed=<s>m/s ... cap=<s>s capped=<bool> wallHit=<bool> wallShortened=<bool> hardCapHit=<bool> ghost=False reported=(...)`；幽灵路径同一行末尾 `ghost=True reported=(...)`（速度取冲刺初速、方向取冲刺方向；`reported` 是本机预摆载具的位置）；`no lead, dash-direction speed=...`；`no lead after wall/cap ... ghost=<bool>`；幽灵路径成功写入后紧跟 `lead recorded for ghost: <s>s / <m>m (dash curve adds <m>m, total predicted <m>m)`。
- `EnforcerExitSmoothing:` → `installed.`；`start distance=<m>m duration=<s>s U=<s>s start=(...) target=(...)`；`done elapsed=<s>s duration=<s>s distance=<m>m`；`abort reason=<stale|disabled|destroyed|authority-lost|death|new-vehicle|teleport ...>`；`hard snap, distance=<m>m, predicted=<m>m, limit=<m>m`（limit 按最后一次冲刺预测里程计算；无预测时 10m）。
- `EnforcerGhostDash:` → 起点 `ghost armed state=<state> pos=(<x>,<y>,<z>) U=<s>s cap=0.25s timeout=<s>s timeoutRTT=<s>s gameRTT=... measuredRTT=... samples=<N> used=<game|measured>`；初始化 `ghost init ... speedCoeff=<m/s>m/s initialSpeed=<m/s>m/s overrideCoeff=<x> duration=<s>s dashDistance=<m>m curve=<source>`；前进 `first step ... lead=<m>m dash=<m>m step=<m>m pos=(...)`；每步节流 `ghost t=<s>s curve=<v> speed=<m/s>m/s traveled=<s>s/<总运动秒>s pos=(...) U=<s>s`；到位 `ghost motion done at t=<s>s (motion=<s>s, predicted=<m>m, wall=<bool>); holding for the seat`；遇墙 `wall stop at t=<s>s ... holding position until handoff/timeout`；载具状态先到 `vehicle state seen ... still simulating`；交接开始 `handoff begin after <s>s (U=<s>s) ghost=(...) seat=(...) gap=<m>m along=<m>m lateral=<m>m traveled=<s>s/<s>s predicted=<m>m holdTimeout=<s>s vehicleStateSeen=<bool> disablePassengerMotor=<bool> vehicle=(...)`；等待节流 `holding for seat <s>s/<s>s gap=<m>m along=<m>m lateral=<m>m seat=(...) ghost=(...)`；追上 `caught up after <s>s; blend to seat from=(...) to=(...) gap=<m>m duration=<s>s`；完成 `handoff done in <s>s (blend <s>s) residual=<m>m; vanilla seat snap resumes`；hold 超时 `hold timeout <s>s, blending to seat anyway (...)` 或 `hold timeout ..., gap=<m>m too far to blend (limit <m>m); vanilla seat snap`；座位弹出 `ghost stopped: seat lost during handoff (ejection); ...`（随后看 ExitSmoothing）；总超时 `ghost stopped: timeout after <s>s (timeout <s>s), vehicle never arrived`（timeout 随当前 RTT 尖峰增长、不缩小；固定 FeedbackFallbackTimeout 覆盖时不变）；其它终止 `ghost stopped: <reason>; pos=(...) elapsed=<s>s handoff=<bool> blendElapsed=<s>s traveled=<s>s/<s>s predicted=<m>m`。

## 5. 异常时一键关掉

- 游戏内：`Settings > Mod Options > RoR2 Latency Mod` 取消对应勾选。启动时已安装的补丁**即时生效**（每个 hook 都通过 `PatchConfig.Toggle/Read` 读取当前值），不用重启。
- 配置文件：`Default\BepInEx\config\com.wray.ror2latency.cfg` 把该项改成 `false`，保存后**重启游戏**。
- P7 特例：启动时为 false 则本局未装 hook，RoO 里中途勾选不算数，必须设 `true` 后重启；默认已为 true。反过来，已开启的 P7 在 RoO 取消勾选是即时停用的。
- 熔断：某个补丁 hook 抛异常或安装失败时只会自己失效并退回原版，其余补丁不受影响。启动汇总会把安装失败显示为 `failed-to-install`；运行中熔断只写日志 `Skill Patch '<Name>' tripped; falling back to vanilla: ...`（汇总行不会更新）。`tripped` 本局不会自动恢复，重启才会重试。

## 6. 同房间前后对比法

1. **基线**：进房前在 RoO 把默认开的 7 项全关（即时生效，无需重启），进同类房间（最好同一主机），用 Enforcer Shift 轻击/蓄力重击和 Loader 钩爪打 1–2 分钟，记录：按键到反馈的停顿、蓄力是否卡、冲刺结束前跳、钩子出现延迟、移动目标命中率、被拉回的幅度。
2. **逐项开**：回大厅/退出后，按第 2 节顺序每次只开一档（RoO 勾选即时生效；改 cfg 则重启），再进同类房间重复同样动作并对比。
3. **P7 单独一轮**：设 `true` → 重启 → 进房；重点对比松手后是否立即前移、以及最后被拉回/前跳的幅度。
4. **同局改配置**：RoO 对“启动时已安装”的补丁即时生效；直接改磁盘上的 cfg 本局不生效，需重启。启动时是 false 的补丁本局不会安装（中途勾选无效）。
5. **记录延迟对照**：事件日志现在同时带 `gameRTT=`（游戏上报）与 `measuredRTT=`（本机松键→座位到达的中位数）以及 `samples=`/`used=`；回传时抄这四个值。下一轮重点看 `measuredRTT` 是否稳定在约 1s、`used=measured` 是否出现，以及它和游戏上报差几倍。也可用按键到反馈的体感估算；注明主机大致地区/是否用加速器。
6. **卡动作复查**：若仍看到冲刺循环姿态，抓 `animation reset ...`、`vanilla exit animation seen ...`、`late=local ...` 三类行；有 `animation reset` 说明本地已复位，若画面仍卡则把 `before=` 和前后 20 行一起回传。

回传日志时粘贴事件前后约 20 行，保留时间戳，并注明当时哪些补丁开/关、做了什么动作。

## 7. 本轮改动（2026-10-24）的已知风险与下一轮关注点

本轮只构建、未在实机运行验证；游戏在跑，未安装到 Default。

- **P7 与 StartLead 强耦合**：只开 GhostDash、关掉 StartLead 时，幽灵会在第一步自停（`StartLead did not report a start`），P7 实际不生效。要测 P7 必须同时开 StartLead。这是为了避免幽灵的提前位移被同步到服务器、而服务器载具仍从原起点开始造成回拉。
- 交接 hold/抑制座位硬贴是新的：只在「本机、本幽灵正在 hold 的那名乘客」上生效；任何异常/关闭/熔断立即释放。若日志出现 `handoff begin` 后玩家仍被明显拖走，检查前缀是否被其它 mod 的 `UpdatePassengerPosition` 补丁绕过。
- 正常期望序列：`ghost armed` → `ghost init` → `lead recorded for ghost` → `first step` →（车状态先到）`vehicle state seen ... still simulating` → `ghost motion done ... holding for the seat` → `handoff begin ...` → `caught up ... blend` → `handoff done ... residual`。如果座位在动作走完前就到达，会直接出现 `handoff begin`，没有 `ghost motion done`，同样正常。若只看到 `handoff begin` 后 `seat lost ... (ejection)`，说明座位在追上之前被弹出，交给 ExitSmoothing；下一轮看 `residual`/ExitSmoothing 的 `start distance`。
- 高 RTT（>0.5s）：lead 仍封顶 0.25s，幽灵按「lead 段 + 完整冲刺曲线」走，**空间终点**与服务器载具一致，但时间上幽灵会先到终点、原地等服务器载具追上（U 越大等得越久）；只有 StartLead 关闭/遇墙缩短/服务器 hit pause 延长冲刺时才可能出现空间不一致，此时交接 blend 会补上（平滑但可能像第二段滑行）。等待超过 hold 上限（`max(2×RTT,0.5s)`，默认 cap 3s）时若座位仍远会退回原版硬贴。若体感突兀，优先调低 `StartLeadCapSeconds` 或关 P7，而不是加大它。
- 尖峰 > `FeedbackFallbackMaxSeconds`（默认 3s）：兜底/幽灵仍会先退 Main，随后到达的载具可能造成一次状态错位；需要时把上限调到 5–10s（会延长真正卡住时的恢复时间）。
- 服务器冲刺被打断（死亡/其它 mod 抢状态）：幽灵可能比服务器终点超前；退场由 ExitSmoothing 兜底，差 >10m 会硬贴。下一轮抓 `seat lost ... (ejection)` 前后的 `ghost=...`、ExitSmoothing `start distance=`。
- 交接完成前若关掉 `EnforcerExitSmoothing`，或它自己熔断，退场可能退回原版硬贴；这是配置取舍，不是 P7 单独能兜住的。
- 不新增网络消息、不改伤害/命中；`NoNeedForSync` 未变。
- 下一轮最需要回传的行：`handoff begin`, `caught up`, `hold timeout`, `seat lost`, `handoff done`, `ghost stopped`, `stuck fallback`, 以及同段的 `EnforcerExitSmoothing: start/done`。

## 8. 本轮 Loader 修正（2026-10-25）的结论、风险与回传清单

本轮同样只构建/跑单测，未实机运行；游戏运行中则未安装。

**偏离根因排序（按证据强度）**

1. **AimLead 旧候选锥过宽（代码可证，日志待新行证实）**：旧实现把 `aim_trajectory_assist_max_change_threshold`（dot 0.8 ≈ 37°）当作 BullseyeSearch 的候选角，且瞄准 HurtBox 中心。原版实际候选是沿射线、半径 ≤3m 的 SphereCast（TrajectoryAimAssist.cs:140-160），最终才用 0.8 dot 兜底。因此旧版可能补到准星外几十度的另一个敌人；在 70m 处 37° 约 57m 横向。新日志的 `vanillaAdj=`/`vanillaToFinal=`/`rawToFinal=` 可判定：若 `vanillaAdj` 很大，说明“补”发生在原版已大幅改向之后；若 `vanillaToFinal` 大，是提前量本身偏转过大。
2. **速度估计噪声/垂直分量**：旧版用最后两条快照差分，包含飞行怪的垂直起伏，也没有多帧一致性。新日志 `speed=new/prev dot span conf vert` 可直接看出：`conf<0.5` 或 `dot` 低时新逻辑不会补。
3. **Loader 实际发射的是 LoaderYankHook**：28 条 FlyState 全是 `LoaderYankHook`，而旧预测只翻 `LoaderHook` 的 allowPrediction；AimLead 对两者都生效。新日志 `FireHook enter prefab='...'` 可确认。
4. **AttackDirectionFix 叠加**：已装 1.2.7，会改 `FireProjectileInfo.position`（相机 aimOrigin），不改 rotation；FireHook 的 position 本来就是 ray.origin，故对方向偏差影响低，但不能从日志完全排除其位移补偿对命中点的间接影响。
5. **预测钩与真钩交接**：旧版对 YankHook 预测未生效，故不是本次“偏离”的原因；纳入后新日志的 handoff 行用于下一轮排除。

**YankHook 预测结论**：纳入。FireYankHook 是 FireHook 的空子类；投射物 prefab 用同一套 ProjectileController/ProjectileGrappleController；预测对象只做本地视觉（已实现：预测钩跳过 `AssignHookReferenceToBodyStateMachine`，不抢 `FireHook.hookInstance`、不挂绳），Yank 拉怪在服务器（YankState 的 `Util.HasEffectiveAuthority(stuckBody)` 在客户端为 false），交接是通用 predictionId/ghost；预测物体在 handoff/`FireHook` 退出/超时后销毁。仅当 prefab `localPlayerAuthority=true` 才启用，否则该变体保持原版并写日志。

**卡顿结论**：未发现本 mod 的每帧重开销。SetState 两个 hook 已加廉价门控（原先每个实体每次 SetState 都分配 Guard 闭包）；每 tick 的 EntityStateMachine.FixedUpdate 前缀在字典门控后才分配；GhostDash 的 CharacterMotor/VehicleSeat hook 增加 activeStates 门控；无每帧反射、LINQ、锁或大字符串预格式化（日志只在事件/每次出钩时格式化，且节流）。Loader 路径全部是每次出钩/单钩飞行，非每帧。若仍卡，需用户说明是掉帧、操作停顿还是回弹，具体技能，以及 BepInEx 控制台是否有 GC/异常。

**原版高延迟“收不回来”**：用户确认未装 mod 也存在。反编译显示 owner 客户端对钩子有状态权威、服务器负责销毁，FireHook 只在 `!hookInstance && hadHookInstance` 时退出；若钩子从未建立引用、Return 后服务器销毁晚到，或松键后状态未推进，技能会滞留。新兜底在 `max(3×RTT,1s)`（上限 `LoaderHookFallbackMaxSeconds`，默认 3s）后按原因本地 `SetNextStateToMain()`，不新增网络消息、不改服务器权威；“未钉住且未 Return”这一支只在钩子自己的 FlyState 已过本 mod 的 deadline 后才触发，避免切断长距离的正常飞行。LoaderRuler 的 `Fix Grapple Range` 已核对为客户端 `+dt/2`（velocity 恒 0，实际约 2×base）；本 mod 检测其开关后按 `base+max(base,ownExtra)` 计算截止，避免重复叠加，并用直接 `ReturnState` 兜住第三方继续加 duration。注意 Ruler 自己的 2×base 不受我们的 0.15s 上限约束；要完全取消它请在 Ruler 里关掉该项。

**未经运行验证的风险**：Ruler 配置键/GUID 假定了 1.1.1；若其设置改名，检测失败会退回“无 Ruler”模型（更长的 deadline，仍受 ownExtra 上限）。Ruler 用 MonoMod 钩同一方法，插件加载顺序可能让我们的 FlyState 前缀被绕过（观察 `FlyState deadline reached` 是否只在 `FlyState enter` 后出现）；兜底不依赖它，仍会在超时后退出。强制 Main 不会销毁服务器侧的旧钩子：它可能继续飞到自然 lifetime（视觉残留），但 `OwnerIsInFiringState` 已为 false，不会再拉人。FireHook 兜底在“钉住且按住键”时刻意不触发；若目标已死亡但 stuck 仍为 true 且玩家一直按住，原版行为保留。预测 YankHook 仅视觉，命中仍由服务器判。

**下一轮请回传**：`LoaderHookPrediction: FireHook enter ...`、`LoaderHookPrediction: predicted ... spawned`、`LoaderHookPrediction: dropped predicted ... (Handoff/FireHookExit/Timeout) ghostAlreadyTransferred=...`、`LoaderAimLead: target=...`（尤其 `conf`, `vanillaAdj`, `vanillaToFinal`, `rawToFinal`, `guard`）、`LoaderFlyTimeout: FlyState enter ... rulerFix=... deadline=...`、`FlyState deadline reached`、`hook state ... -> ...`、`owner fallback -> Main`、`FireHook exit`、`LoaderRuler detected`。

## 9. Loader HUD 逐项检查表（2026-10-25）

本轮 6 个 HUD 与 2 项修复均只做了反编译核对 + 编译 + 纯函数测试，**未实机验证**；下面写的是按代码推出的期望，不是已经看到的画面。截图/录屏请尽量保持 UI 缩放与分辨率不变，并附 `LogOutput.log`。

### 9.0 通用开法与截图约定

- 开法：游戏内 `Settings > Mod Options > RoR2 Latency Mod`；修复项在根级，HUD 项在分类 `Loader HUD`，勾选 `<显示名>：启用`。没装 RoO 时改 `BepInEx\config\com.wray.ror2latency.cfg` 的 `[Loader HUD]` 段（键名见各项）。**修复类默认 `true`，6 个 HUD 默认 `false`。**
- 重启规则：从“6 个 HUD 全关”的启动状态首次启用任一项，必须重启（全关时模块不安装 HUD 钩子）；已有任一项开启时再开其它项即时生效（RoO 仍会提示重启，可忽略）。关闭最后一项后宿主下一帧销毁，重开同样要重启。
- 通用可调项：每项都有 `HUD_<Key>_AnchorX` / `AnchorY`（0–1，默认 0.5/0.5 = HUD 安全区中心，约屏幕中心/准星附近）、`OffsetX` / `OffsetY`（父容器宽/高比例，非像素）、`Scale`（0.5–3，默认 1）、`Color`（默认暖白 `1, 0.94, 0.85`）、`Opacity`（0–1，默认 1）、`FontSize`（8–48）、`BackgroundOpacity`（0–1，默认 0.25；0=无底色）。文字统一为：标签 60% 字号、67% 透明度，数值用等宽数字，居中，0.1 黑色描边，后面可选一层 25% 黑色细底。
- 截图通用：用 F12/Steam 截图即可；UI 文字较小，建议截 1920×1080 原图并在回传时说明“哪一项、当时状态”，不要裁到只剩几个像素。动态过程（钩爪飞行、拳击命中）优先录 3–5s 视频。

### 9.1 修复1：钩爪射程补偿（默认开）

1. **怎么开**：RoO 根级 `钩爪飞行超时补偿`（`[Loader]` / `LoaderFlyTimeout`，默认 `true`，总开关）与 `提供 LoaderRuler 等价补偿`（`[Loader]` / `LoaderRulerEquivalent`，默认 `true`）。必须打开 `LoaderFlyTimeout`，关闭它则该补丁整体不运行；`LoaderRulerEquivalent` 只决定用哪种截止公式。
2. **期望外观**：没有 HUD。表现为高延迟下钩爪不再提前回收：朝 70–80m 的墙面/边缘出钩，钩子能飞到接近最远端并完成拉人；钉住后即使拉人状态晚到也不会瞬间弹回。日志出现 `FlyState enter ... deadline=...`，到期出现 `FlyState deadline reached`。
3. **可调参数**：`[Loader]` / `FlyTimeoutMargin`（自身余量，默认 0.10s，0–4）、`FlyTimeoutExtraMaxSeconds`（余量硬上限，默认 0.15s，0–4）、`LoaderReturnSpeedMultiplier`（收回速度倍率，默认 1，1–10）；`[Enforcer]` / `EnforcerDashLengthMultiplier`（冲刺距离倍率，默认 1，1–10）、`LoaderHookFallbackMaxSeconds`（owner 兜底，默认 3.0s，0.5–10）。`LoaderRulerEquivalent` 控制是否用 `base + max(base, 余量)`（约 2×base）；关掉后，未装 LoaderRuler 时退回 `base + 余量`（装了且其修复开启时仍取 `max`，避免叠加；装了但其修复关闭时尊重其关闭态，同样退回 `base + 余量`）。
4. **建议截图/录屏**：出钩前先露一下准星与目标距离，然后录“钩爪刚出钩 0.5s 内 → 到达最远点 → 开始拉人”的整段（静态截图只能证明钩子存在，证明不了没有提前回收）。同时在 BepInEx 控制台截 `FlyState enter` / `deadline` / `deadline reached` 三行。

### 9.2 修复2：准心（默认开）

1. **怎么开**：RoO 根级 `钩爪准心修正`（`[Loader]` / `LoaderCrosshair`，默认 `true`）。
2. **期望外观**：仍是游戏原版 Loader 钩爪准心（原色、原尺寸，无 mod 自定义颜色）。默认超时翻倍下射程指示到 178.25m（80 × 2.225 + 0.25，含固定 50ms 同步余量）：100m 的细杆/墙边应亮，170m 也可能亮（属预期），约 180m 以上不应亮；FlyTimeout 开但翻倍关闭（无 Ruler 且 `LoaderRulerEquivalent` 关，或 Ruler 在场但 `Fix Grapple Range` 显式关）为 134.25m（80 × 1.675 + 0.25）；FlyTimeout 关、熔断或主机跳过而 Ruler 关则为原版 80.25m。观战他人 Loader 时准星不套用本地配置（保持 prefab 原值）。蓄力拳期间（准心切成 `StraightBracketCrosshair`）仍显示 `HookIndicatorInRange` 射程提示；冲刺/挥拳期间不再被冲刺准心替换。
3. **可调参数**：仅开关；无位置/颜色/尺寸配置（沿用游戏资产）。
4. **建议截图**：屏幕中心区域取景，三张 A/B 对照：普通站立瞄 100m 目标（默认应亮）与约 180m 目标（不应亮；170m 属于可亮范围）；蓄力拳满蓄力瞬间；冲刺中。每张注明 `LoaderCrosshair=true/false`，并记录 `LoaderFlyTimeout` / `LoaderRulerEquivalent` / Ruler `Fix Grapple Range` 三态以便按上表核对。该项是纯本地 HUD 修正，主机/单人同样生效。开关语义：启动时关闭则本次会话不安装补丁，需要重启才能开启；启动时开启后，运行中关闭会立即恢复原版射程并移除蓄力指示器，再次开启即时生效；滑条（余量）改后即时生效。

### 9.3 钩爪剩余时间 HUD（默认关）

1. **怎么开**：RoO `Loader HUD` → `钩爪剩余时间：启用`；cfg `[Loader HUD]` / `HUD_GrappleDuration_Enabled = true`（默认 `false`）。
2. **期望外观**：钩爪存在（飞行/钉住/回收）期间，在安全区中心**右上方**出现两行居中文字：小字 `钩爪剩余`，下一行大号等宽 `3.42s`（格式固定两位小数 + `s`）。1/3 以上用配置色；剩余比例低于 1/3 后从配置色渐变到纯红。
3. **可调参数**：`HUD_GrappleDuration_AnchorX/Y`（默认 0.5/0.5）、`OffsetX`（0.055）、`OffsetY`（0.100）、`Scale`（1）、`Color`、`Opacity`（1）、`FontSize`（22）、`BackgroundOpacity`（0.25）。危险红色阈值固定 1/3，不可调。
4. **建议截图**：钩爪刚出钩 0.5s 内截一张（显示较大数值），再在剩余低于 1/3（默认 5s 寿命下约 1.6s）截一张（应从配置色开始渐变到纯红）。取景为屏幕中心向右上偏移的区域，保留准星便于定位。

### 9.4 钩爪长度 HUD（默认关）

1. **怎么开**：RoO `钩爪长度：启用`；cfg `HUD_GrappleLength_Enabled = true`（默认 `false`）。
2. **期望外观**：钩爪存在期间，在中心**右上方、比“钩爪剩余”更靠近中心**的位置显示小字 `钩爪长度` + 大号 `42.0 m`（一位小数 + ` m`）。无状态变色，始终用配置色。
3. **可调参数**：`HUD_GrappleLength_AnchorX/Y`（0.5/0.5）、`OffsetX`（0.055）、`OffsetY`（0.065）、`Scale`、`Color`、`Opacity`、`FontSize`（18）、`BackgroundOpacity`。
4. **建议截图**：钩爪钉在远处墙面时截图，取景同 9.3；回传时说明大致实际距离（例如“训练场两块平台之间约 35m”），便于核对数值口径。

### 9.5 速度文字 HUD（默认关）

1. **怎么开**：RoO `速度文字：启用`；cfg `HUD_SpeedText_Enabled = true`（默认 `false`）。
2. **期望外观**：在中心**右下方**显示小字 `速度` + 大号 `12.3 m/s`（一位小数）。字体会随速度从配置字号平滑放大到最多 1.6 倍（100 m/s 封顶）。蓄力拳（`BaseChargeFist`）或挥拳（`BaseSwingChargedFist`）期间，数值下方多一行 65% 小字 `瞄准 0.87`（点积 -1..1，两位小数；越小越偏）。颜色固定配置色，不随状态变色。
3. **可调参数**：`HUD_SpeedText_AnchorX/Y`（0.5/0.5）、`OffsetX`（0.110）、`OffsetY`（-0.060）、`Scale`、`Color`、`Opacity`、`FontSize`（20）、`BackgroundOpacity`。速度放大曲线与 1.6 倍上限固定在代码里。
4. **建议截图**：三张——平地正常跑动；高速下落/被击飞；蓄力拳刚进入蓄力（显示 `瞄准` 行）。取景中心右下，保留角色与速度状态。

### 9.6 拳速记录 HUD（默认关）

1. **怎么开**：RoO `拳速记录：启用`；cfg `HUD_PunchRecord_Enabled = true`（默认 `false`）。条数另外在 cfg `[Loader HUD]` / `HUD_PunchRecord_Count`（默认 3，范围 1–10）；**该项不在 RoO**，首次启用并显示时才会在 cfg 里创建；手改后需重启（BepInEx 不热读 cfg）。
2. **期望外观**：在中心**左上方**显示小字 `拳速`，下面每行 `角度 : 速度`，例如 `87 : 32`（角度是速度与瞄准方向点积映射的 0–100 整数百分比，速度是整数 m/s），最新命中在上，最多 N 行。速度越大的行字号越大（1.0–1.6 倍，100 m/s 封顶）；若同一拳在首次命中前先触发雷霆重击（Thunderslam）命中，该行整体灰色 `#808080` 且缩到 70%。一次拳击打中多个目标只记一行（取首次命中）。
3. **可调参数**：`HUD_PunchRecord_AnchorX/Y`（0.5/0.5）、`OffsetX`（-0.220）、`OffsetY`（0.120）、`Scale`、`Color`（仅非灰行用）、`Opacity`、`FontSize`（18）、`BackgroundOpacity`，以及 `HUD_PunchRecord_Count`（1–10）。灰化颜色/70% 固定在代码里。
4. **建议截图**：在训练场或真实房间连续打 2–3 拳命中，等记录出现后截取中心左上区域；另截一张“雷霆重击先命中”的灰行。回传时说明每拳的目标数（验证一拳一条）。**已知限制**：客户端观战远端 Loader 时没有 authority 命中事件，不会新增记录；此场景不必按缺失记录报 bug，但请注明是观战。

### 9.7 速度方向图标 HUD（默认关）

1. **怎么开**：RoO `速度方向图标：启用`；cfg `HUD_VelocityIcon_Enabled = true`（默认 `false`）。
2. **期望外观**：中心**左下方**出现一个 32 UI px 的箭头（默认用游戏自带 `Textures/MiscIcons/texBasicArrowRight`；资源找不到时是内置三线段箭头，形状略简陋属正常回退）。箭头指向速度在屏幕平面的投影方向，并随速度从 0→20 m/s 平滑渐显；颜色按瞄准与速度方向的点积变化：反向红 → 垂直黄 → 同向绿（默认渐变模式）。速度几乎垂直进出屏幕时图标隐藏。
3. **可调参数**：`HUD_VelocityIcon_AnchorX/Y`（0.5/0.5）、`OffsetX`（-0.090）、`OffsetY`（-0.080）、`Scale`（0.5–3，乘在 32px 上）、`Color`、`Opacity`。`FontSize` 与 `BackgroundOpacity` 对图标无效果（无文字/无底板）；渐变模式下 `Color` 只取 alpha，RGB 由状态色覆盖。静态色/渐变开关（`UseAimGradient`）与图标路径（`IconAddress`）是代码常量，不在 RoO/cfg。
4. **建议截图**：分别录/截：直线跑动、斜向冲刺、下落，各配“瞄准方向与速度同向 / 反向”两张，取中心左下。若图标方向与运动方向明显不符，请连游戏画面一起录，便于判断屏幕投影口径。

### 9.8 钩爪能量 HUD（实验，默认关）

1. **怎么开**：RoO `钩爪能量（实验）：启用`；cfg `HUD_GrappleEnergy_Enabled = true`（默认 `false`）。
2. **期望外观**：仅在钩爪已经钉住（`ProjectileStickOnImpact.stuck`）时，在中心**右侧略上**显示小字 `钩爪能量（实验）` + 大号能量值，例如 `1234 J` / `1.2 kJ` / `1.23 MJ`。数值按 10 Hz 刷新（肉眼略跳正常，不是逐帧平滑）。钩爪飞行中、未钉住、找不到钩爪时隐藏。颜色固定配置色。
3. **可调参数**：`HUD_GrappleEnergy_AnchorX/Y`（0.5/0.5）、`OffsetX`（0.120）、`OffsetY`（0.020）、`Scale`、`Color`、`Opacity`、`FontSize`（16）、`BackgroundOpacity`。公式、0.5m 无底深渊兜底、10 Hz 刷新固定在代码里。
4. **建议截图**：钩爪钉在高处/低处墙面各一张，另在悬崖边向虚空出钩一张（验证 0.5m 兜底不出现 NaN/负值），取中心右侧。回传时写明是否钉住、钩爪大概高度与绳长，便于核对数量级。
