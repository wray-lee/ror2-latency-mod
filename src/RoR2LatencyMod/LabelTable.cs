using System;
using System.Collections.Generic;
using System.Globalization;

namespace RoR2LatencyMod;

public enum LabelLanguage
{
    Chinese,
    English,
}

/// <summary>One user-visible string in both shipped languages.</summary>
public readonly struct LabelText
{
    public LabelText(string zh, string en)
    {
        Zh = zh;
        En = en;
    }

    public string Zh { get; }

    public string En { get; }

    public string For(LabelLanguage language)
        => language == LabelLanguage.English ? En : Zh;
}

/// <summary>
/// Every user-visible string this mod owns: HUD labels, HUD/config names, and BepInEx/RoO
/// descriptions. Pure data with no game or Unity types, so tests can load it without the game.
/// </summary>
public static class LabelTable
{
    public static readonly IReadOnlyDictionary<string, LabelText> All =
        new Dictionary<string, LabelText>(StringComparer.Ordinal)
        {
            // ---- HUD readout labels ----
            ["hud.label.grappleDuration"] = new("钩爪剩余", "Hook time"),
            ["hud.label.grappleLength"] = new("钩爪长度", "Hook length"),
            ["hud.label.speed"] = new("速度", "Speed"),
            ["hud.label.aim"] = new("瞄准", "Aim"),
            ["hud.label.punchSpeed"] = new("拳速", "Punch"),
            ["hud.label.grappleEnergy"] = new("钩爪能量（实验）", "Hook energy (exp.)"),

            // ---- HUD names used in config sections and RoO prefixes ----
            ["hud.name.grappleDuration"] = new("钩爪剩余时间", "Hook duration"),
            ["hud.name.grappleLength"] = new("钩爪长度", "Hook length"),
            ["hud.name.speedText"] = new("速度文字", "Speed text"),
            ["hud.name.punchRecord"] = new("拳速记录", "Punch speed log"),
            ["hud.name.velocityIcon"] = new("速度方向图标", "Velocity direction icon"),
            ["hud.name.grappleEnergy"] = new("钩爪能量（实验）", "Hook energy (experimental)"),

            // ---- HUD config descriptions; {0} = HUD display name ----
            ["hud.desc.enabled"] = new(
                "{0}：启用（默认关，纯客户端）。启动时若全部 HUD 均为关，则首次启用需重启：此时模块不安装任何 HUD 钩子。" +
                "已有任一 HUD 启用时，本项开关即时生效；关闭最后一项后再启用同样需重启。其余参数可在游戏内实时调整。",
                "{0}: enabled (off by default, client-only). If every HUD was off at startup, the first enable needs a restart because the module installs no HUD hook in that state. " +
                "While at least one HUD was enabled at startup, this switch applies live; turning the last one off and back on needs a restart. All other settings apply live."),
            ["hud.desc.anchorX"] = new(
                "{0}：锚点 X（HUD 安全区归一化坐标，0=左、1=右；默认 {1:0.000}）。",
                "{0}: anchor X (normalized HUD-safe-area coordinate, 0 = left, 1 = right; default {1:0.000})."),
            ["hud.desc.anchorY"] = new(
                "{0}：锚点 Y（HUD 安全区归一化坐标，0=下、1=上；默认 {1:0.000}）。",
                "{0}: anchor Y (normalized HUD-safe-area coordinate, 0 = bottom, 1 = top; default {1:0.000})."),
            ["hud.desc.offsetX"] = new(
                "{0}：相对锚点的横向偏移（父容器宽度的比例，随分辨率和 UI 缩放自适应；默认 {1:0.000}）。",
                "{0}: horizontal offset from the anchor (fraction of the parent width, resolution/UI-scale independent; default {1:0.000})."),
            ["hud.desc.offsetY"] = new(
                "{0}：相对锚点的纵向偏移（父容器高度的比例，随分辨率和 UI 缩放自适应；默认 {1:0.000}）。",
                "{0}: vertical offset from the anchor (fraction of the parent height, resolution/UI-scale independent; default {1:0.000})."),
            ["hud.desc.scale"] = new(
                "{0}：整体缩放倍率（默认 1）。",
                "{0}: overall scale multiplier (default 1)."),
            ["hud.desc.color"] = new(
                "{0}：数值主色（默认暖白）。危险/良好状态色由各项 HUD 的显示规则覆盖。",
                "{0}: primary value color (default warm white). Per-HUD danger/good state colors still override it."),
            ["hud.desc.opacity"] = new(
                "{0}：整体不透明度（0–1，默认 1）。",
                "{0}: overall opacity (0-1, default 1)."),
            ["hud.desc.fontSize"] = new(
                "{0}：数值字号（标签为它的 60%；默认 {1:0}）。",
                "{0}: value font size (the label uses 60% of it; default {1:0})."),
            ["hud.desc.backgroundOpacity"] = new(
                "{0}：可选半透明细底的不透明度（0 = 无底色；默认 0.25）。",
                "{0}: opacity of the optional thin translucent backing (0 = no backing; default 0.25)."),
            ["hud.desc.punchCount"] = new(
                "拳速记录：显示最近多少次蓄力拳命中（1-10，默认 3）。位置/字号/颜色仍用 HUD_PunchRecord_* 通用项。",
                "Punch speed log: how many recent charged-fist hits to show (1-10, default 3). Position, size, and color still use the generic HUD_PunchRecord_* settings."),

            // ---- HUD RoO option names; {0} = HUD display name ----
            ["hud.option.enable"] = new("{0}：启用", "{0}: Enabled"),
            ["hud.option.color"] = new("{0}：颜色", "{0}: Color"),
            ["hud.option.fontSize"] = new("{0}：数值字号", "{0}: Value font size"),
            ["hud.option.scale"] = new("{0}：缩放", "{0}: Scale"),
            ["hud.option.opacity"] = new("{0}：不透明度", "{0}: Opacity"),
            ["hud.option.backgroundOpacity"] = new("{0}：底色不透明度", "{0}: Background opacity"),
            ["hud.option.anchorX"] = new("{0}：锚点 X", "{0}: Anchor X"),
            ["hud.option.anchorY"] = new("{0}：锚点 Y", "{0}: Anchor Y"),
            ["hud.option.offsetX"] = new("{0}：偏移 X", "{0}: Offset X"),
            ["hud.option.offsetY"] = new("{0}：偏移 Y", "{0}: Offset Y"),

            // ---- Patch/slider config descriptions ----
            ["patch.LoaderHookPrediction.desc"] = new(
                "Loader 钩爪本地预测：按键后立即显示钩子，不再等一个 RTT（票04）。默认开（用户决定；仅视觉预测，出现隐形钩或音效截断可关）。开关即时生效；启动时若为关，则重新开启需重启。",
                "Loader hook local prediction: show the hook immediately on input instead of waiting one RTT (ticket 04). Default on (user decision; visual-only prediction, turn off if you see an invisible hook or cut-off sound). The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.LoaderFlyTimeout.desc"] = new(
                "Loader 钩爪飞行超时补偿：钩爪已钉住但拉人状态晚到时，延长 FlyState 超时（票05）。默认开。开关即时生效；启动时若为关，则重新开启需重启。",
                "Loader hook flight timeout compensation: extend the FlyState timeout when the hook is stuck but the pull state arrives late (ticket 05). Default on. The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.LoaderRulerEquivalent.desc"] = new(
                "LoaderRuler 等价补偿：不装 LoaderRuler 也把客户端钩爪飞行截止按 base + max(base, 余量) 计算（等价其超时翻倍）；" +
                "装了 LoaderRuler 时统一同一截止、不叠加；若 LoaderRuler 的 Fix Grapple Range 被显式关闭，则尊重其关闭态、不再翻倍。默认开。即时生效。",
                "LoaderRuler-equivalent compensation: without LoaderRuler, compute the client hook flight deadline as base + max(base, margin) (equivalent to doubling its timeout); " +
                "with LoaderRuler installed both use the same deadline and do not stack; if LoaderRuler's Fix Grapple Range is explicitly off, honor that and do not double. Default on. Applies live."),
            ["patch.LoaderCrosshair.desc"] = new(
                "Loader 钩爪准心：显示完整最大射程，蓄力拳时保留射程指示，冲刺时不被冲刺准心覆盖（仅 Loader 本体，纯客户端）。默认开。" +
                "开关即时生效；启动时若为关，则重新开启需重启。",
                "Loader hook crosshair: show the full maximum range, keep the range indicator while charging a fist, and do not let the dash crosshair replace it (Loader only, client-side). Default on. " +
                "The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.LoaderAimLead.desc"] = new(
                "Loader 钩爪提前量：按目标速度与延迟修正瞄准（票06）。默认开。开关即时生效；启动时若为关，则重新开启需重启。",
                "Loader hook aim lead: correct aim from target velocity and latency (ticket 06). Default on. The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.AimLeadStrength.desc"] = new(
                "钩爪提前量强度倍率：0 = 原版不补偿，1 = 按 目标速度×(U+实际插值延迟+飞行时间) 补偿；" +
                "还会按速度估计置信度自动缩放（多帧一致才补）。",
                "Hook aim-lead strength: 0 = vanilla, no compensation; 1 = compensate by target velocity x (U + interpolation latency + flight time). " +
                "It also scales by velocity-estimate confidence (only consistent multi-frame estimates are used)."),
            ["patch.AimLeadMaxSeconds.desc"] = new(
                "钩爪提前量上限（秒的目标行程，默认 0.15s）：越小越保守；0 = 不加提前量。",
                "Hook aim-lead cap (seconds of target travel, default 0.15 s): smaller is more conservative; 0 = no lead."),
            ["patch.FlyTimeoutMargin.desc"] = new(
                "钩爪 FlyState 自身余量（秒，不含延迟；默认 0.1s）。这是叠加到原版飞行时间上的自有余量；未命中时最多比原版晚这么久回收。" +
                "启用等价补偿/超时翻倍时，实际余量取 max(base, 本值)，因此可能大于本值。",
                "Loader FlyState own margin (seconds, latency excluded; default 0.1 s). This is the extra flight time added on top of vanilla; a missed hook is recycled at most this much later. " +
                "When Ruler-equivalent doubling is active, the actual extra is max(base, this value), so it can exceed this setting."),
            ["patch.FlyTimeoutExtraMaxSeconds.desc"] = new(
                "自有飞行超时余量的硬上限（秒，默认 0.15）。只限制 FlyTimeoutMargin 这一路输入；启用等价补偿/超时翻倍时实际余量取 max(base, FlyTimeoutMargin)，" +
                "base 默认 2×FlyTimeoutMargin≈0.222s，可能高于该上限。",
                "Hard cap for the mod's own flight-time margin (seconds, default 0.15). It only clamps the FlyTimeoutMargin input; when Ruler-equivalent doubling is active the actual extra is max(base, FlyTimeoutMargin), " +
                "and base defaults to 2 x FlyTimeoutMargin ~ 0.222 s, which can exceed this cap."),
            ["patch.LoaderHookFallbackMaxSeconds.desc"] = new(
                "钩爪兜底上限（秒）：FireHook 超过 max(3×RTT,1s) 仍无钩子/返回未销毁时，本地退回 Main；0.5–10，默认 3。",
                "Hook fallback cap (seconds): if FireHook exceeds max(3 x RTT, 1 s) with no hook or with a return that never destroys it, return to Main locally; 0.5-10, default 3."),
            ["patch.LoaderReturnSpeedMultiplier.desc"] = new(
                "钩爪收回速度倍率（1–10，默认 1 = 原版）：只改自己钩爪的 ReturnState 初速与加速度；钩子位置由本客户端上报，主机会更早销毁，可更快再出钩。即时生效。",
                "Hook return speed multiplier (1-10, default 1 = vanilla): scales only your own hook's ReturnState speed and acceleration; your client reports the hook position, so the host destroys it sooner and you can fire again earlier. Applies live."),
            ["patch.EnforcerFeedback.desc"] = new(
                "Enforcer 冲刺即时反馈与兜底：本地立即播反馈，载具状态迟迟不到时自行退出（票07）。默认开。开关即时生效；启动时若为关，则重新开启需重启。",
                "Enforcer dash immediate feedback and fallback: play feedback locally at once and exit by yourself when the vehicle state keeps arriving late (ticket 07). Default on. The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.FeedbackFallbackTimeout.desc"] = new(
                "Enforcer 兜底超时（秒）：0 = 按有效 RTT 自动取 max(3×RTT, 1.0s)，上限见 FeedbackFallbackMaxSeconds；" +
                "有效 RTT 取 max(游戏上报, 实测中位数)，避免尖峰/样本滞后导致提前强退；>0 = 固定值。",
                "Enforcer fallback timeout (seconds): 0 = derive max(3 x RTT, 1.0 s) from the effective RTT, capped by FeedbackFallbackMaxSeconds; " +
                "effective RTT is max(game-reported, measured median) so spikes/lagging samples do not force an early exit; >0 = fixed value."),
            ["patch.FeedbackFallbackMaxSeconds.desc"] = new(
                "自动兜底超时、幽灵冲刺超时与交接 hold 超时的上限（秒）。" +
                "FeedbackFallbackTimeout=0 时兜底/幽灵取 max(3×RTT,1.0s) 并受此上限约束；" +
                "交接 hold 取 max(2×RTT,0.5s) 也受此上限约束。真实房间实测载具有时 >1.5s 才到，故默认 3.0。",
                "Upper bound (seconds) for the automatic fallback timeout, ghost-dash timeout, and handoff hold timeout. " +
                "With FeedbackFallbackTimeout=0, fallback/ghost use max(3 x RTT, 1.0 s) capped by this value; " +
                "handoff hold uses max(2 x RTT, 0.5 s) capped by it too. Measured vehicles sometimes take >1.5 s to arrive in real rooms, so the default is 3.0."),
            ["patch.InitialLatencyCapSeconds.desc"] = new(
                "还没有实测往返样本时，游戏上报 RTT 折算出的单向延迟 U 上限（秒）。" +
                "避免首次冲刺被虚高的 RTT 拖住；拿到实测样本后改用 min(游戏上报, 实测)。默认 0.5。",
                "Cap (seconds) for the one-way latency U derived from the game-reported RTT while no measured round trip exists. " +
                "It keeps the first dash from being held up by an inflated RTT; once measured samples exist, min(game-reported, measured) is used. Default 0.5."),
            ["patch.EnforcerStartLead.desc"] = new(
                "Enforcer 冲刺起点前推：松键时把起点沿冲刺方向前推 速度×U（票08）。默认开。开关即时生效；启动时若为关，则重新开启需重启。",
                "Enforcer dash start lead: on release, advance the start point along the dash direction by speed x U (ticket 08). Default on. The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.StartLeadCapSeconds.desc"] = new(
                "起点前推最多折算多少秒的冲刺位移（票08）。",
                "Maximum seconds of dash travel the start lead may convert (ticket 08)."),
            ["patch.EnforcerDashLengthMultiplier.desc"] = new(
                "Enforcer 冲刺距离倍率（1–10，默认 1 = 原版）：拉长自己冲刺的时长，速度不变，距离按倍率增加；主机按客户端上报的时长执行。命中/伤害仍由主机结算。即时生效（下一次冲刺）。",
                "Enforcer dash length multiplier (1-10, default 1 = vanilla): lengthens your own dash duration at the same speed, so distance scales; the host runs the duration your client reports. Hits and damage stay host-side. Applies from the next dash."),
            ["patch.StartLeadMaxMeters.desc"] = new(
                "起点前推的硬距离上限（米）；0 = 不额外封顶，仅受 StartLeadCapSeconds 约束。",
                "Hard distance cap for the start lead (meters); 0 = no extra cap, limited only by StartLeadCapSeconds."),
            ["patch.EnforcerExitSmoothing.desc"] = new(
                "Enforcer 退场平滑：下冲刺时用短插值到达退场位置，消除前跳（票09）。默认开。开关即时生效；启动时若为关，则重新开启需重启。",
                "Enforcer exit smoothing: interpolate briefly to the exit position when leaving a dash, removing the forward jump (ticket 09). Default on. The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.EnforcerGhostDash.desc"] = new(
                "Enforcer 幽灵冲刺：松键后本地立即模拟 StartLead 的 lead 段 + 完整冲刺曲线，" +
                "只抑制本机该乘客的座位硬贴；载具追上后 0.1–0.2s 并入，超时/弹出交给退场平滑（P7）。" +
                "依赖 EnforcerStartLead（关闭/熔断时幽灵自动停）；实验项，默认开（用户决定）。开关即时生效；启动时若为关，则重新开启需重启。",
                "Enforcer ghost dash: after release, simulate the StartLead segment and the full dash curve locally, " +
                "suppress only this client's hard seat snap, then blend over 0.1-0.2 s once the vehicle catches up; timeouts/ejections go through exit smoothing (P7). " +
                "Depends on EnforcerStartLead (the ghost stops when that is off/tripped); experimental, on by default (user decision). The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.EnforcerLocalEnd.desc"] = new(
                "Enforcer 本地提前结束：幽灵冲刺到位即本地播放退出动画并释放移动/技能，不再等服务器 eject 的 0.2–1.1s 尾延迟；" +
                "服务器退出状态到达时幂等吸收（不重播动画、不二次贴位）（P9）。依赖 EnforcerGhostDash，默认开。" +
                "开关即时生效；启动时若为关，则重新开启需重启。",
                "Enforcer local early end: when the ghost dash reaches its predicted end, play the exit locally and release movement/skills instead of waiting 0.2-1.1 s for the server eject; " +
                "the vanilla exit arrival is absorbed idempotently (no replay, no second snap) (P9). Depends on EnforcerGhostDash; on by default. The switch applies live; if it was off at startup, re-enabling it needs a restart."),
            ["patch.ExitSmoothSeconds.desc"] = new(
                "退场位置插值时长（秒），限制在 0.1–0.2s。",
                "Exit-position interpolation duration (seconds), clamped to 0.1-0.2 s."),

            // ---- RoO option names ----
            ["patch.LoaderHookPrediction.name"] = new("钩爪本地预测", "Hook local prediction"),
            ["patch.LoaderFlyTimeout.name"] = new("钩爪飞行超时补偿", "Hook flight timeout compensation"),
            ["patch.LoaderRulerEquivalent.name"] = new("提供 LoaderRuler 等价补偿", "LoaderRuler-equivalent compensation"),
            ["patch.LoaderCrosshair.name"] = new("钩爪准心修正", "Hook crosshair fix"),
            ["patch.LoaderAimLead.name"] = new("钩爪提前量", "Hook aim lead"),
            ["patch.AimLeadStrength.name"] = new("提前量强度倍率", "Aim lead strength"),
            ["patch.AimLeadMaxSeconds.name"] = new("提前量上限（秒目标行程）", "Aim lead cap (s of travel)"),
            ["patch.FlyTimeoutMargin.name"] = new("飞行超时余量（秒）", "Flight timeout margin (s)"),
            ["patch.FlyTimeoutExtraMaxSeconds.name"] = new("飞行超时余量输入上限（秒）", "Flight timeout margin input cap (s)"),
            ["patch.LoaderHookFallbackMaxSeconds.name"] = new("钩爪兜底上限（秒）", "Hook fallback cap (s)"),
            ["patch.LoaderReturnSpeedMultiplier.name"] = new("钩爪收回速度倍率", "Hook return speed multiplier"),
            ["patch.EnforcerFeedback.name"] = new("Enforcer 冲刺即时反馈", "Enforcer dash immediate feedback"),
            ["patch.FeedbackFallbackTimeout.name"] = new("兜底超时（秒）", "Fallback timeout (s)"),
            ["patch.FeedbackFallbackMaxSeconds.name"] = new("兜底/幽灵超时上限（秒）", "Fallback/ghost timeout cap (s)"),
            ["patch.InitialLatencyCapSeconds.name"] = new("初始单向延迟上限（秒）", "Initial one-way latency cap (s)"),
            ["patch.EnforcerStartLead.name"] = new("Enforcer 起点前推", "Enforcer start lead"),
            ["patch.StartLeadCapSeconds.name"] = new("起点前推上限（秒）", "Start lead cap (s)"),
            ["patch.StartLeadMaxMeters.name"] = new("起点前推距离上限（米）", "Start lead distance cap (m)"),
            ["patch.EnforcerDashLengthMultiplier.name"] = new("Enforcer 冲刺距离倍率", "Enforcer dash length multiplier"),
            ["patch.EnforcerExitSmoothing.name"] = new("Enforcer 退场平滑", "Enforcer exit smoothing"),
            ["patch.EnforcerGhostDash.name"] = new("幽灵冲刺（实验）", "Ghost dash (experimental)"),
            ["patch.EnforcerLocalEnd.name"] = new("本地提前结束", "Local early end"),
            ["patch.ExitSmoothSeconds.name"] = new("退场平滑时长（秒）", "Exit smoothing duration (s)"),
        };

    public static string Get(string key, LabelLanguage language)
        => All[key].For(language);

    public static string Format(string key, LabelLanguage language, params object[] args)
        => string.Format(CultureInfo.InvariantCulture, Get(key, language), args);

    /// <summary>
    /// Pure fallback decision: Chinese only when the language starts with "zh" and the current
    /// font can render the labels; every other case (including unknown/failed probes) is English.
    /// </summary>
    public static LabelLanguage Select(string languageCode, bool fontSupportsCjk)
        => !string.IsNullOrEmpty(languageCode)
           && languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
           && fontSupportsCjk
            ? LabelLanguage.Chinese
            : LabelLanguage.English;
}
