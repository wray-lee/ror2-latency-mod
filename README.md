# RoR2 Latency Mod

BepInEx mod for Risk of Rain 2 (GUID `com.wray.ror2latency`, version 0.1.0). Client-side latency compensation for **Enforcer Shield Tackle** and **Loader grapple** when you are not the host. It supersedes LoaderRuler: the grapple-range fix and reworked Loader HUDs are included, so LoaderRuler can be uninstalled.

## What it does

As a remote client you wait one round trip before the host's dash vehicle, hook flight or pull state reaches you. This mod predicts and plays the local parts immediately, then reconciles when the host state arrives.

- All compensation runs on your client. Hits and damage remain host-authoritative.
- `NetworkCompatibility(NoNeedForSync)`: vanilla hosts and other players do not need the mod. It is not part of the networked mod list and does not affect joining lobbies.

## Requirements

- BepInExPack 5.4.2122
- R2API Core 5.3.0 (hard dependency; used for the network-compatibility marker)
- Risk Of Options is optional (soft dependency) for in-game settings; `HUD_PunchRecord_Count` is cfg-only and is not exposed there. Without it, edit `BepInEx/config/com.wray.ror2latency.cfg`.

## Features

Config values are read live. "Host" means the feature also runs when you are hosting or playing solo. Keys are shown without their section: Loader and Enforcer keys live under `[Loader]` and `[Enforcer]`; HUD keys under `[Loader HUD]`.

### Loader

| Feature | Config key | Default | Host |
|---|---|---|---|
| Hook local prediction: a predicted hook appears on input instead of one RTT later (visual only; its hold timeout is capped by `FeedbackFallbackMaxSeconds`) | `LoaderHookPrediction` | `true` | No |
| Hook flight timeout compensation: Ruler-equivalent client deadline plus owner-side FireHook fallback | `LoaderFlyTimeout` | `true` | Yes |
| — LoaderRuler-equivalent deadline: base + max(base, min(`FlyTimeoutMargin`, `FlyTimeoutExtraMaxSeconds`)) | `LoaderRulerEquivalent` | `true` | Yes |
| — Own flight margin (clamped by the next key before the max) | `FlyTimeoutMargin` | `0.1` s (0–4) | Yes |
| — Cap applied to the own margin before the max; it does not cap the applied extra | `FlyTimeoutExtraMaxSeconds` | `0.15` s (0–4) | Yes |
| — FireHook fallback cap | `LoaderHookFallbackMaxSeconds` | `3` s (0.5–10) | Yes |
| Hook crosshair: shows the nominal range at the configured timeout plus the fixed 50 ms sync grace (current latency is excluded), keeps the range indicator while charging a fist, and is not replaced by the sprint crosshair | `LoaderCrosshair` | `true` | Yes |
| Hook aim lead for moving targets (sub-keys: `AimLeadStrength` `1`, 0–3; `AimLeadMaxSeconds` `0.15` s, 0–0.5) | `LoaderAimLead` | `true` | No |
| Hook return speed multiplier (gameplay buff; see below) | `LoaderReturnSpeedMultiplier` | `1` | Yes |

### Enforcer

| Feature | Config key | Default | Host |
|---|---|---|---|
| Dash feedback and stuck fallback: play feedback locally at once and exit locally when the vehicle state is late (sub-keys: `FeedbackFallbackTimeout` `0` s, 0–10, 0 = auto; `FeedbackFallbackMaxSeconds` `3` s, 0.5–10; `InitialLatencyCapSeconds` `0.5` s, 0.05–2) | `EnforcerFeedback` | `true` | No |
| Dash start lead: advance the reported start point along the dash direction by speed x one-way latency (sub-keys: `StartLeadCapSeconds` `0.25` s, 0–1 but effectively 0–0.25 because values above 0.25 s are ignored; `StartLeadMaxMeters` `0` m, 0–20, 0 = no extra cap) | `EnforcerStartLead` | `true` | No |
| Exit smoothing: a short interpolation to the exit position removes the 2–3 m forward jump (sub-key: `ExitSmoothSeconds` `0.15` s, 0.1–0.2) | `EnforcerExitSmoothing` | `true` | No |
| Ghost dash: simulate the dash locally after release and blend into the host vehicle when it catches up. Experimental; depends on `EnforcerStartLead` | `EnforcerGhostDash` | `true` | No |
| Local early end: end the dash locally when the ghost reaches its predicted end. Depends on `EnforcerGhostDash` | `EnforcerLocalEnd` | `true` | No |
| Dash length multiplier (gameplay buff; see below) | `EnforcerDashLengthMultiplier` | `1` | Yes |

Shared `[Enforcer]` keys: `FeedbackFallbackTimeout` and `FeedbackFallbackMaxSeconds` also control the `EnforcerGhostDash` hold timeout, and `FeedbackFallbackMaxSeconds` also caps the `LoaderHookPrediction` predicted-hook hold timeout. `InitialLatencyCapSeconds` caps the latency estimate used by every Enforcer patch (feedback, start lead, exit smoothing, ghost dash, local end). All three stay active when `EnforcerFeedback` is off.

### HUD

All HUD elements are off by default. They are local UI and follow your HUD target, so they also work while hosting and while spectating a Loader. The punch log only records your own (or, when hosting, locally authoritative) hits.

| Element | Config key | Default | Host |
|---|---|---|---|
| Hook duration: remaining hook lifetime; turns red below 1/3 | `HUD_GrappleDuration_Enabled` | `false` | Yes |
| Hook length: distance from the aim origin to the hook | `HUD_GrappleLength_Enabled` | `false` | Yes |
| Speed text: speed in m/s, plus the aim dot while charging or swinging a charged fist | `HUD_SpeedText_Enabled` | `false` | Yes |
| Punch speed log: the last N charged-fist hits as "alignment % : speed" (0–100, higher = closer to aim), newest first (`HUD_PunchRecord_Count` `3`, 1–10; cfg only, not in Risk Of Options) | `HUD_PunchRecord_Enabled` | `false` | Yes |
| Velocity direction icon: arrow showing the velocity direction on the HUD plane | `HUD_VelocityIcon_Enabled` | `false` | Yes |
| Hook energy (experimental): energy estimate while the hook is stuck | `HUD_GrappleEnergy_Enabled` | `false` | Yes |

Shared per-HUD settings: `HUD_<Key>_AnchorX`/`_AnchorY` (0–1, default 0.5), `_OffsetX`/`_OffsetY` (−0.5–0.5; defaults 0.055/0.100, 0.055/0.065, 0.110/−0.060, −0.220/0.120, −0.090/−0.080, 0.120/0.020 in table order), `_Scale` (0.5–3, default 1), `_Color` (default 1, 0.94, 0.85), `_Opacity` (0–1, default 1), `_FontSize` (8–48; defaults 22, 18, 20, 18, 24, 16), `_BackgroundOpacity` (0–1, default 0.25).

If every HUD was off at startup (or you turned the last one off), enabling a HUD needs a restart.

## Optional gameplay buffs (beyond the default compensation)

These go beyond latency compensation. At their defaults they add no further reach or speed on top of the default Ruler-equivalent compensation, which itself roughly doubles the hook's flight time and reach (see Known limitations).

| Option | Config key | Default | Range | Host |
|---|---|---|---|---|
| Hook flight margin | `FlyTimeoutMargin` | `0.1` s | 0–4 s | Yes |
| Cap applied to the hook flight margin before `max(base, ...)`; it does not cap the applied extra | `FlyTimeoutExtraMaxSeconds` | `0.15` s | 0–4 s | Yes |
| Hook return speed | `LoaderReturnSpeedMultiplier` | `1` | 1–10 | Yes |
| Enforcer dash length | `EnforcerDashLengthMultiplier` | `1` | 1–10 | Yes |

With the Ruler-equivalent deadline active, the own extra is first clamped to `FlyTimeoutExtraMaxSeconds`: extra = max(base, min(`FlyTimeoutMargin`, `FlyTimeoutExtraMaxSeconds`)), where base is about 0.222 s for the current hook (80 m at 360 m/s). With the shipped defaults (margin 0.1 s, cap 0.15 s) that min is at most 0.15 s, below base, so the deadline stays base + base ≈ 0.444 s: raising `FlyTimeoutMargin` alone does not extend reach because the cap clamps it first. To extend reach, raise `FlyTimeoutExtraMaxSeconds` above base as well; with both keys at 4 the hook flies for about 4.2 s, roughly 1.5 km.

Warning: these options rely on the host trusting client-reported values. The hook is client-authoritative and reports its position, and the host executes the dash duration your client reports without range validation. Ask your host before using them in other people's lobbies.

## Known limitations

- `LoaderHookPrediction` is visual only. The predicted hook cannot stick or pull; the real host-spawned hook does that. Turn it off if you see an invisible hook or cut-off sound.
- `EnforcerLocalEnd` only changes your client. The host's copy of the seat can remain occupied briefly after your local dash ends, so other players may see you held in the dash pose for a short time.
- LoaderRuler is not required. If both are installed and LoaderRuler's "Fix Grapple Range" is on, LoaderRuler controls the deadline: its doubling is applied once and does not stack, and this mod's `LoaderRulerEquivalent` is ignored. If LoaderRuler's fix is explicitly off, no doubling is added even with `LoaderRulerEquivalent` on. Without LoaderRuler, `LoaderRulerEquivalent` alone decides.
- With default settings the Loader hook is not vanilla: the default Ruler-equivalent compensation roughly doubles its flight time and reach (crosshair ≈ 178 m vs 80 m nominal vanilla) both as a client and when hosting or solo. Disable `LoaderFlyTimeout` for vanilla reach; setting `LoaderRulerEquivalent` to `false` and `FlyTimeoutMargin` to `0` removes the doubling but keeps the fixed 50 ms sync grace.
- `EnforcerGhostDash` is experimental and depends on `EnforcerStartLead`; `EnforcerLocalEnd` depends on `EnforcerGhostDash`. A dependent feature stops when its parent is off or trips.
- A patch that was off at startup is not installed, so turning it on later needs a restart. Turning a feature off applies live; turning it back on is live only if it was on at startup. HUD settings are read live while the HUD module is active; it only installs at startup if at least one HUD is on. If every HUD was off at startup, or you turn the last one off, enabling one needs a restart.
- If a patch throws, it disables itself for the session and falls back to vanilla; it does not crash the game.

## Troubleshooting

- Every feature has its own switch (or multiplier); turn off the misbehaving feature to return it to vanilla behaviour. Set multipliers to 1 for vanilla movement.
- Logs are written to `BepInEx/LogOutput.log`. Filter for `RoR2LatencyMod`, `RoR2 Latency Mod`, or the patch name (`LoaderHookPrediction`, `LoaderFlyTimeout`, `LoaderCrosshair`, `LoaderAimLead`, `EnforcerFeedback`, `EnforcerStartLead`, `EnforcerExitSmoothing`, `EnforcerGhostDash`, `EnforcerLocalEnd`).
- Multiplayer: vanilla hosts and players do not need the mod. If a problem appears only in one lobby, note whether optional buffs were enabled and whether the host runs other mods.
- LoaderRuler users: see the coexistence note above.

---

# RoR2 Latency Mod（中文）

适用于 Risk of Rain 2 的 BepInEx mod（GUID `com.wray.ror2latency`，版本 0.1.0）。为 **Enforcer 盾牌冲撞（Shield Tackle）** 与 **Loader 钩爪** 提供纯客户端延迟补偿，仅在你不是主机时生效（标注“主机也运行”的功能除外）。本 mod 取代 LoaderRuler：包含其钩爪射程修复并重做了 Loader HUD，因此可以卸载 LoaderRuler。

## 功能说明

作为远程客户端，主机生成的冲刺载具、钩爪飞行与拉人状态要经过一个往返才能到你这里。本 mod 在本地立即预测并播放，主机状态到达后再对账。

- 所有补偿只在你自己的客户端运行，命中与伤害仍由主机判定。
- `NetworkCompatibility(NoNeedForSync)`：原版主机和其他玩家都不需要安装本 mod；本 mod 不进入联网 mod 清单，也不影响搜索或加入房间。

## 前置要求

- BepInExPack 5.4.2122
- R2API Core 5.3.0（硬依赖，用于网络兼容标记）
- Risk Of Options 可选（软依赖），用于游戏内设置；`HUD_PunchRecord_Count` 只能改 cfg，不在 Risk Of Options 中。不装时直接修改 `BepInEx/config/com.wray.ror2latency.cfg`。

## 功能列表

配置值即时生效。“主机也运行”表示你当主机或单人游戏时该功能是否也生效。下表配置键省略段名：Loader 与 Enforcer 的键位于 `[Loader]`、`[Enforcer]` 段，HUD 的键位于 `[Loader HUD]` 段。

### Loader

| 功能 | 配置键 | 默认值 | 主机也运行 |
|---|---|---|---|
| 钩爪本地预测：按下即显示预测钩，不再等一个往返（仅视觉；保持超时上限见 `FeedbackFallbackMaxSeconds`） | `LoaderHookPrediction` | `true` | 否 |
| 钩爪飞行超时补偿：等价 LoaderRuler 的客户端截止时间，并增加 owner 侧 FireHook 兜底 | `LoaderFlyTimeout` | `true` | 是 |
| —— LoaderRuler 等价截止：base + max(base, min(`FlyTimeoutMargin`, `FlyTimeoutExtraMaxSeconds`)) | `LoaderRulerEquivalent` | `true` | 是 |
| —— 自有飞行余量（先被下一项截断，再参与 max） | `FlyTimeoutMargin` | `0.1` 秒（0–4） | 是 |
| —— 自有飞行余量的截断上限（不封顶最终实际余量） | `FlyTimeoutExtraMaxSeconds` | `0.15` 秒（0–4） | 是 |
| —— FireHook 兜底上限 | `LoaderHookFallbackMaxSeconds` | `3` 秒（0.5–10） | 是 |
| 钩爪准心：显示按配置超时计算的名义射程加固定 50 毫秒同步宽限（不含当前延迟），蓄力拳时保留射程指示，不被疾跑（sprint）准心覆盖 | `LoaderCrosshair` | `true` | 是 |
| 移动目标钩爪提前量（子键：`AimLeadStrength` `1`，0–3；`AimLeadMaxSeconds` `0.15` 秒，0–0.5） | `LoaderAimLead` | `true` | 否 |
| 钩爪收回速度倍率（玩法加强，见下文） | `LoaderReturnSpeedMultiplier` | `1` | 是 |

### Enforcer

| 功能 | 配置键 | 默认值 | 主机也运行 |
|---|---|---|---|
| 冲刺即时反馈与卡住兜底：本地立即播放反馈，载具状态晚到时本地退出（子键：`FeedbackFallbackTimeout` `0` 秒，0–10，0 = 自动；`FeedbackFallbackMaxSeconds` `3` 秒，0.5–10；`InitialLatencyCapSeconds` `0.5` 秒，0.05–2） | `EnforcerFeedback` | `true` | 否 |
| 冲刺起点前推：按速度 × 单程延迟沿冲刺方向前推上报起点（子键：`StartLeadCapSeconds` `0.25` 秒，0–1，实际生效上限 0.25、超过 0.25 的值被忽略；`StartLeadMaxMeters` `0` 米，0–20，0 = 不额外封顶） | `EnforcerStartLead` | `true` | 否 |
| 退场平滑：用短插值到达退场位置，消除 2–3 米前跳（子键：`ExitSmoothSeconds` `0.15` 秒，0.1–0.2） | `EnforcerExitSmoothing` | `true` | 否 |
| 幽灵冲刺：松键后本地模拟冲刺，载具追上后平滑并入；实验项，依赖 `EnforcerStartLead` | `EnforcerGhostDash` | `true` | 否 |
| 本地提前结束：幽灵到达预测终点时本地结束冲刺状态；依赖 `EnforcerGhostDash` | `EnforcerLocalEnd` | `true` | 否 |
| 冲刺距离倍率（玩法加强，见下文） | `EnforcerDashLengthMultiplier` | `1` | 是 |

`[Enforcer]` 共享键：`FeedbackFallbackTimeout` 与 `FeedbackFallbackMaxSeconds` 也控制 `EnforcerGhostDash` 的保持超时，`FeedbackFallbackMaxSeconds` 还封顶 `LoaderHookPrediction` 预测钩的保持超时；`InitialLatencyCapSeconds` 封顶所有 Enforcer 补丁共用的延迟估计（反馈、起点前推、退场平滑、幽灵冲刺、本地提前结束）。关闭 `EnforcerFeedback` 后这些键仍然生效。

### HUD

所有 HUD 默认关闭。HUD 为本地显示，跟随 HUD 目标，因此主机/单人以及观战 Loader 时也可显示。拳速记录只记录本人（或主机本地权威）的命中。

| 元素 | 配置键 | 默认值 | 主机也运行 |
|---|---|---|---|
| 钩爪剩余时间：钩爪剩余寿命，低于 1/3 时变红 | `HUD_GrappleDuration_Enabled` | `false` | 是 |
| 钩爪长度：瞄准原点到钩爪的距离 | `HUD_GrappleLength_Enabled` | `false` | 是 |
| 速度文字：速度（m/s），蓄力或挥出蓄力拳时附带瞄准点积 | `HUD_SpeedText_Enabled` | `false` | 是 |
| 拳速记录：最近 N 次蓄力拳命中，按“对齐度% : 速度”显示（0–100，越高越接近瞄准方向），最新在上（`HUD_PunchRecord_Count` `3`，1–10；仅 cfg，不在 Risk Of Options 中） | `HUD_PunchRecord_Enabled` | `false` | 是 |
| 速度方向图标：在 HUD 平面上显示速度方向的箭头 | `HUD_VelocityIcon_Enabled` | `false` | 是 |
| 钩爪能量（实验）：钩住期间的能量估算 | `HUD_GrappleEnergy_Enabled` | `false` | 是 |

HUD 通用设置：`HUD_<Key>_AnchorX`/`_AnchorY`（0–1，默认 0.5）、`_OffsetX`/`_OffsetY`（−0.5–0.5；按上表顺序默认 0.055/0.100、0.055/0.065、0.110/−0.060、−0.220/0.120、−0.090/−0.080、0.120/0.020）、`_Scale`（0.5–3，默认 1）、`_Color`（默认 1、0.94、0.85）、`_Opacity`（0–1，默认 1）、`_FontSize`（8–48；默认 22、18、20、18、24、16）、`_BackgroundOpacity`（0–1，默认 0.25）。

如果启动时所有 HUD 都是关闭的（或你把最后一个 HUD 关掉），再启用 HUD 需要重启游戏。

## 可选玩法加强（默认补偿之外）

以下选项超出延迟补偿范围。在默认值下，它们不会在默认等价补偿之外再增加射程或速度；但默认等价补偿本身就会把钩爪飞行时间和射程大约翻倍（见“已知限制”）。

| 选项 | 配置键 | 默认值 | 范围 | 主机也运行 |
|---|---|---|---|---|
| 钩爪飞行余量 | `FlyTimeoutMargin` | `0.1` 秒 | 0–4 秒 | 是 |
| 钩爪飞行余量的截断上限（先于 `max(base, ...)` 生效，不封顶最终实际余量） | `FlyTimeoutExtraMaxSeconds` | `0.15` 秒 | 0–4 秒 | 是 |
| 钩爪收回速度 | `LoaderReturnSpeedMultiplier` | `1` | 1–10 | 是 |
| Enforcer 冲刺距离 | `EnforcerDashLengthMultiplier` | `1` | 1–10 | 是 |

启用等价补偿时，自有余量先被 `FlyTimeoutExtraMaxSeconds` 截断：extra = max(base, min(`FlyTimeoutMargin`, `FlyTimeoutExtraMaxSeconds`))；当前钩爪的 base 约为 0.222 秒（80 米 / 360 米每秒）。按出厂默认（余量 0.1 秒、上限 0.15 秒），min 最多 0.15 秒、低于 base，截止时间保持 base + base ≈ 0.444 秒：只提高 `FlyTimeoutMargin` 不会增加射程，因为它先被上限截断。要增加射程，必须同时把 `FlyTimeoutExtraMaxSeconds` 提高到 base 以上；两项都设为 4 时钩爪约飞行 4.2 秒，约 1.5 公里。

警告：这些选项依赖主机信任客户端上报的值。钩爪由客户端权威并自行上报位置；主机也会直接执行客户端上报的冲刺时长，不做范围校验。在别人的房间使用前请先征得主机同意。

## 已知限制

- `LoaderHookPrediction` 仅视觉预测：预测钩不会钉住或拉动，真正的命中/拉动来自主机生成的钩爪。若出现隐形钩或音效被截断，可关闭该开关。
- `EnforcerLocalEnd` 只改本地：本地提前结束后，主机侧的座位可能短暂保留，其他玩家可能看到你多保持一小会儿冲刺姿态。
- 不需要 LoaderRuler。若同时安装且 LoaderRuler 的 “Fix Grapple Range” 开启，则由 LoaderRuler 决定截止时间：翻倍只生效一次、不会叠加，本 mod 的 `LoaderRulerEquivalent` 被忽略。若 LoaderRuler 的该修复被显式关闭，则即使 `LoaderRulerEquivalent` 开启也不会翻倍。未安装 LoaderRuler 时，只有 `LoaderRulerEquivalent` 决定是否翻倍。
- 默认设置下 Loader 钩爪并非原版：默认等价补偿会把飞行时间与射程大约翻倍（准心约 178 米，原版名义射程约 80 米），无论作为客户端还是主机/单人。要恢复原版射程请关闭 `LoaderFlyTimeout`；把 `LoaderRulerEquivalent` 设为 `false` 且 `FlyTimeoutMargin` 设为 `0` 只会去掉翻倍，仍保留固定 50 毫秒同步宽限。
- `EnforcerGhostDash` 为实验项，依赖 `EnforcerStartLead`；`EnforcerLocalEnd` 依赖 `EnforcerGhostDash`。父项关闭或熔断时，依赖项自动停止。
- 启动时关闭的补丁不会被安装，之后开启需要重启。关闭功能即时生效；重新开启只有在启动时已开启的情况下才即时生效。HUD 模块只有在启动时至少有一个 HUD 开启才会安装，模块活跃期间 HUD 设置即时生效；如果启动时所有 HUD 都关闭（或你把最后一个 HUD 关掉），再启用需要重启。
- 补丁内部异常时只会熔断该补丁并退回原版，不会使游戏崩溃。

## 故障排查

- 每个功能都有独立开关（或倍率）；关掉出问题的功能即可回到原版行为，倍率设为 1 即为原版移动。
- 日志在 `BepInEx/LogOutput.log`。可过滤 `RoR2LatencyMod`、`RoR2 Latency Mod` 或补丁名（`LoaderHookPrediction`、`LoaderFlyTimeout`、`LoaderCrosshair`、`LoaderAimLead`、`EnforcerFeedback`、`EnforcerStartLead`、`EnforcerExitSmoothing`、`EnforcerGhostDash`、`EnforcerLocalEnd`）。
- 联机：原版主机和玩家不需要安装本 mod。若只在某个房间出问题，请记录是否开启了可选加强、主机是否装了其他 mod。
- LoaderRuler 用户：见上面的共存说明。
