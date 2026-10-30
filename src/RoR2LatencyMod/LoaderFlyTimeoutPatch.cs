// 票05 LoaderFlyTimeout：钩爪飞行超时与 owner 侧兜底。
//
// 反编译核对（F:/ror2-decomp/RoR2src，只读）：
// - FlyState.OnEnter：duration = maxTravelDistance / ProjectileSimple.velocity
//   （ProjectileGrappleController.cs:123-127）；FixedUpdateBehavior 在 stuck 未到且 duration <= fixedAge 时
//   SetNextState(ReturnState)（同文件 :129-159）。
// - 客户端权威链：ProjectileManager.FireProjectileServer :586-588 在 localPlayerAuthority 时
//   SpawnWithClientAuthority → owner 客户端 isAuthority=true（日志证实 authority=true）；
//   ReturnState 只有服务器在 Overshoots 后 Destroy（:351-357），owner 的 FireHook 要等销毁复制回来才退
//   （FireHook.cs:79 `isAuthority && !hookInstance && hadHookInstance`）。高延迟下这条“状态→服务器→销毁→回来”
//   链天然要 1–2 个 RTT，原版钩子会滞留；不是本 mod 造成的，但本 mod 不能加重。
// - 原版缺口：FireHook.SetHookReference 由钩子 Start 调用（ProjectileGrappleController.cs:443），
//   若钩子对象从未在 owner 侧建立引用，hadHookInstance 永远 false，FireHook 永不退出；或 Return 已发生
//   但服务器销毁消息迟迟不到，玩家也无法再次出钩。本补丁在 owner 侧超时后退回 Main（纯本地状态机，
//   复用游戏原有 msg48，不新增网络消息）。
// - 兜底计时不能只看 FireHook 总时长：长按 1.5s 再松手时，ReturnState 才刚开始；若按总时长立即
//   退回 Main，投掷物会因 OwnerIsInFiringState 变 false 每 tick 重入 ReturnState（msg48 抖动）。
//   因此 ReturnState / 松手后的 stuck 状态各自从进入/松手时刻重新计一个完整的兜底窗口
//   （PredictionMath.ReturnFallbackWithinGrace）；hookGone/never-seen 分支保持不变。
// - 状态机归属（反编译核实）：投掷物 GameObject 自带 EntityStateMachine
//   （[RequireComponent]，ProjectileGrappleController.cs:9），FlyState/ReturnState/Grip/Yank 都跑在它
//   上面（:105/:156/:160）；customName=="Hook" 的 ESM 在玩家身体上（OwnerInfo ctor :41-49），跑的是
//   FireHook。所以这里必须取投掷物自己的 ESM；FindByCustomName(hook,"Hook") 恒为 null。
// - stuck 是服务器 SyncVar（ProjectileStickOnImpact.cs:63-64,96），客户端钩子的碰撞被关
//   （ProjectileController.cs:408-423、ProjectileNetworkTransform.cs:139-144），无法本地判定。高延迟下
//   真实命中的同步会晚于客户端原截止，故 FlyState 截止额外加 U + 50ms 固定步余量后才强制
//   ReturnState（#3）；stuck 已同步时绝不强制。余量上限 1s。
// - LoaderRuler 1.1.1（GUID dolso.loaderruler）在客户端 FlyState.FixedUpdateBehavior 里对
//   isAuthority && rigidbody.velocity.sqrMagnitude==0 每 tick 加 dt/2；客户端钩子是
//   ProjectileNetworkTransform 的插值/运动学对象（ProjectileNetworkTransform.cs:139-143），velocity 恒 0，
//   所以它的实际到期约 2×base。默认无条件按 base + max(base, ownExtra) 截止（与 Ruler 等价，卸载
//   Ruler 行为不变）；Ruler 在场且 fix 开着时只统一同一截止、不把补偿再叠一倍；Ruler 在场但 fix
//   被用户显式关掉时尊重其关闭态，退回 base + ownExtra（#6）。关闭本补丁开关时同样退回旧规则。
//   到期用 SetNextState(ReturnState) 强制回收，避免第三方 mod 再把 duration 加回去。

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using EntityStates;
using EntityStates.Loader;
using HarmonyLib;
using RoR2;
using RoR2.Projectile;
using UnityEngine;

namespace RoR2LatencyMod;

public sealed class LoaderFlyTimeoutPatch : SkillPatch
{
    private const string Tag = "LoaderFlyTimeout";
    private const string LoaderRulerGuid = "dolso.loaderruler";
    private const string LoaderRulerSection = "Fixes";
    private const string LoaderRulerKey = "Fix Grapple Range";

    private static LoaderFlyTimeoutPatch instance;

    /// <summary>
    /// True while this patch actually applies: installed, live switch on, not tripped. The
    /// crosshair reads this instead of the raw config so a failed install or a trip cannot
    /// advertise a range the hook no longer has.
    /// </summary>
    public static bool RuntimeActive => instance != null && instance.ShouldRun();

    // LoaderRuler's range fix, the return speed and the fallback all act on the local authority
    // hook only (every hook body checks isAuthority), so they apply when hosting as well.
    protected override bool RequiresRemoteClient => false;

    private static Type flyStateType;
    private static MethodInfo flyOnEnter;
    private static MethodInfo flyFixedUpdateBehavior;
    private static MethodInfo fireHookFixedUpdate;
    private static MethodInfo fireHookOnExit;
    private static FieldInfo durationField;
    private static FieldInfo grappleField;
    private static FieldInfo hookInstanceField;
    private static FieldInfo outerField;
    private static PropertyInfo isAuthorityProperty;
    private static PropertyInfo fixedAgeProperty;
    private static Type returnStateType;
    private static FieldInfo returnSpeedField;
    private static FieldInfo returnAccelerationField;

    private static bool? loaderRulerFixEnabled;
    private static bool loaderRulerPresent;
    private static double lastRulerCheckTime = double.NegativeInfinity;

    private readonly ConditionalWeakTable<object, FlightWatch> flights =
        new ConditionalWeakTable<object, FlightWatch>();

    private readonly ConditionalWeakTable<EntityState, HookWatch> hooks =
        new ConditionalWeakTable<EntityState, HookWatch>();

    private sealed class FlightWatch
    {
        public bool Forced;
    }

    private sealed class HookWatch
    {
        public bool Started;
        public double EnterTime;
        public double StateEnterTime;
        public double ReleasedTime;
        public bool KeyHeld;
        public double HookSeenTime;
        public bool SawHook;
        public bool HookGone;
        public bool Forced;
        public GameObject HookObject;
        public EntityStateMachine HookMachine;
        public string HookStateName;
    }

    public LoaderFlyTimeoutPatch(
        Func<bool> isEnabled,
        Func<bool> provideRulerEquivalent,
        Func<float> flyTimeoutMarginSeconds,
        Func<float> flyTimeoutExtraMaxSeconds,
        Func<float> loaderHookFallbackMaxSeconds,
        Func<float> returnSpeedMultiplier,
        DiagLog diag = null,
        LatencyEstimator latency = null)
        : base("LoaderFlyTimeout", PatchSide.Client, isEnabled, diag, latency)
    {
        ProvideRulerEquivalent = provideRulerEquivalent;
        FlyTimeoutMarginSeconds = flyTimeoutMarginSeconds;
        FlyTimeoutExtraMaxSeconds = flyTimeoutExtraMaxSeconds;
        LoaderHookFallbackMaxSeconds = loaderHookFallbackMaxSeconds;
        ReturnSpeedMultiplier = returnSpeedMultiplier;
    }

    /// <summary>Default-on LoaderRuler-equivalent client timeout doubling.</summary>
    public Func<bool> ProvideRulerEquivalent { get; }

    /// <summary>Own flight-timeout extra (seconds), independent of one-way latency.</summary>
    public Func<float> FlyTimeoutMarginSeconds { get; }

    /// <summary>Hard ceiling for the extra actually applied above vanilla (seconds).</summary>
    public Func<float> FlyTimeoutExtraMaxSeconds { get; }

    /// <summary>Ceiling for the owner-side FireHook fallback (seconds).</summary>
    public Func<float> LoaderHookFallbackMaxSeconds { get; }

    /// <summary>Owner-side ReturnState speed multiplier (1 = vanilla).</summary>
    public Func<float> ReturnSpeedMultiplier { get; }

    protected override void Install()
    {
        flyStateType = AccessTools.TypeByName("RoR2.Projectile.ProjectileGrappleController+FlyState");
        if (flyStateType == null)
        {
            throw new MissingMemberException("RoR2.Projectile.ProjectileGrappleController+FlyState not found");
        }

        flyOnEnter = AccessTools.Method(flyStateType, "OnEnter", Type.EmptyTypes);
        flyFixedUpdateBehavior = AccessTools.Method(flyStateType, "FixedUpdateBehavior", Type.EmptyTypes);
        durationField = AccessTools.Field(flyStateType, "duration");
        grappleField = AccessTools.Field(flyStateType, "grappleController");
        isAuthorityProperty = AccessTools.Property(flyStateType, "isAuthority");
        fixedAgeProperty = AccessTools.Property(flyStateType, "fixedAge");
        returnStateType = flyStateType.DeclaringType?.GetNestedType("ReturnState", BindingFlags.NonPublic);
        outerField = AccessTools.Field(typeof(EntityState), "outer");

        fireHookFixedUpdate = AccessTools.Method(typeof(FireHook), "FixedUpdate", Type.EmptyTypes);
        fireHookOnExit = AccessTools.Method(typeof(FireHook), "OnExit", Type.EmptyTypes);
        hookInstanceField = AccessTools.Field(typeof(FireHook), "hookInstance");
        returnSpeedField = AccessTools.Field(returnStateType, "returnSpeed");
        returnAccelerationField = AccessTools.Field(returnStateType, "returnSpeedAcceleration");
        var returnOnEnter = AccessTools.Method(returnStateType, "OnEnter", Type.EmptyTypes);

        if (flyOnEnter == null || flyFixedUpdateBehavior == null || durationField == null ||
            grappleField == null || isAuthorityProperty == null || fixedAgeProperty == null ||
            returnStateType == null || outerField == null ||
            fireHookFixedUpdate == null || fireHookOnExit == null || hookInstanceField == null ||
            returnSpeedField == null || returnAccelerationField == null || returnOnEnter == null)
        {
            throw new MissingMemberException(
                "FlyTimeout members missing: " +
                $"FlyState.OnEnter={flyOnEnter != null}, FixedUpdateBehavior={flyFixedUpdateBehavior != null}, " +
                $"duration={durationField != null}, grappleController={grappleField != null}, " +
                $"isAuthority={isAuthorityProperty != null}, fixedAge={fixedAgeProperty != null}, " +
                $"ReturnState={returnStateType != null}, EntityState.outer={outerField != null}, " +
                $"FireHook.FixedUpdate={fireHookFixedUpdate != null}, FireHook.OnExit={fireHookOnExit != null}, " +
                $"FireHook.hookInstance={hookInstanceField != null}, " +
                $"ReturnState.returnSpeed={returnSpeedField != null}, " +
                $"ReturnState.returnSpeedAcceleration={returnAccelerationField != null}, " +
                $"ReturnState.OnEnter={returnOnEnter != null}");
        }

        instance = this;
        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            flyOnEnter,
            postfix: new HarmonyMethod(typeof(LoaderFlyTimeoutPatch), nameof(OnEnterPostfix)));
        harmony.Patch(
            flyFixedUpdateBehavior,
            prefix: new HarmonyMethod(typeof(LoaderFlyTimeoutPatch), nameof(FixedUpdateBehaviorPrefix)));
        harmony.Patch(
            fireHookFixedUpdate,
            postfix: new HarmonyMethod(typeof(LoaderFlyTimeoutPatch), nameof(FireHookFixedUpdatePostfix)));
        harmony.Patch(
            fireHookOnExit,
            postfix: new HarmonyMethod(typeof(LoaderFlyTimeoutPatch), nameof(FireHookOnExitPostfix)));
        harmony.Patch(
            returnOnEnter,
            postfix: new HarmonyMethod(typeof(LoaderFlyTimeoutPatch), nameof(ReturnOnEnterPostfix)));

        Diag?.Info(
            "LoaderFlyTimeout: installed (Ruler-equivalent FlyState deadline, " +
            "Ruler coexistence without double stacking, owner-side FireHook fallback, return speed).");
    }

    private static void ReturnOnEnterPostfix(EntityState __instance)
    {
        var self = instance;
        if (self == null || !self.ShouldRun())
        {
            return;
        }

        self.Guard(() => self.ScaleReturnSpeed(__instance));
    }

    // The owner client drives the hook's position (ProjectileNetworkTransform sends it with
    // CmdSetPositionAndRotationFromClient and the server copy is kinematic), so speeding up the
    // authority's ReturnState really brings the hook back sooner and the server's overshoot
    // destroy fires sooner too.
    private void ScaleReturnSpeed(EntityState state)
    {
        double multiplier = PredictionMath.BuffMultiplier(
            ReturnSpeedMultiplier != null ? ReturnSpeedMultiplier() : 1f);
        if (multiplier <= 1.0 || !(bool)isAuthorityProperty.GetValue(state))
        {
            return;
        }

        float speed = (float)returnSpeedField.GetValue(state);
        float acceleration = (float)returnAccelerationField.GetValue(state);
        returnSpeedField.SetValue(state, (float)(speed * multiplier));
        returnAccelerationField.SetValue(state, (float)(acceleration * multiplier));
        Diag?.Info(
            $"{Tag}: ReturnState speed x{multiplier:F2} ({speed:F0} -> {speed * multiplier:F0} m/s).");
    }

    private static void OnEnterPostfix(object __instance)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        self.Guard(() => self.LogEnter(__instance));
    }

    private void LogEnter(object state)
    {
        if (!ShouldRun())
        {
            return;
        }

        var grapple = grappleField.GetValue(state) as ProjectileGrappleController;
        if (grapple == null)
        {
            return;
        }

        double baseDuration = BaseDurationSeconds(grapple);
        if (double.IsNaN(baseDuration))
        {
            return;
        }

        // Refresh the other mod's toggle once per flight, not per tick.
        loaderRulerFixEnabled = DetectLoaderRulerFix();

        bool authority = (bool)isAuthorityProperty.GetValue(state);
        bool predicted = grapple.GetComponent<ProjectileController>() is { isPrediction: true };
        string prefabName = grapple.gameObject != null ? grapple.gameObject.name : "<null>";
        double ownExtra = EffectiveOwnExtraSeconds();
        double oneWay = CurrentOneWayLatencySeconds();
        double deadline = CurrentFlyDeadlineSeconds(baseDuration);
        var networkTransform = grapple.GetComponent<ProjectileNetworkTransform>();
        double interpDelay = networkTransform != null
            ? networkTransform.positionTransmitInterval * networkTransform.interpolationFactor
            : double.NaN;

        Diag?.Info(
            $"LoaderFlyTimeout: FlyState enter '{prefabName}' predicted={predicted} authority={authority} " +
            $"lifetime={(grapple.GetComponent<ProjectileSimple>()?.lifetime ?? float.NaN):F1}s " +
            $"base={baseDuration:F3}s ownExtra={ownExtra:F3}s deadline={deadline:F3}s " +
            $"stuckSyncGrace={PredictionMath.StuckSyncGraceSeconds(oneWay):F3}s " +
            $"rulerEquivalent={(RulerEquivalentEnabled() ? "on" : "off")} " +
            $"rulerPresent={loaderRulerPresent} " +
            $"rulerFix={(loaderRulerFixEnabled.Value ? "on" : "off")} " +
            $"interp={(double.IsNaN(interpDelay) ? double.NaN : interpDelay * 1000.0):F0}ms " +
            $"U={(Latency != null && Latency.TryGetOneWayLatency(out var u) ? u * 1000.0 : 0.0):F0}ms " +
            (authority ? "" : "(no authority: deadline inert)"));
    }

    private static void FixedUpdateBehaviorPrefix(object __instance)
    {
        var self = instance;
        if (self == null || !self.ShouldRun())
        {
            return;
        }

        self.Guard(() => self.EnforceDeadline(__instance));
    }

    /// <summary>
    /// Before the deadline keep duration above fixedAge so vanilla/other mods cannot return early;
    /// at the deadline force ReturnState directly so another mod re-adding duration cannot stall us.
    /// </summary>
    private void EnforceDeadline(object state)
    {
        if (!(bool)isAuthorityProperty.GetValue(state))
        {
            return;
        }

        var grapple = grappleField.GetValue(state) as ProjectileGrappleController;
        if (grapple == null)
        {
            return;
        }

        var stick = grapple.GetComponent<ProjectileStickOnImpact>();
        if (stick != null && stick.stuck)
        {
            return; // GripState/YankState owns the exit from here.
        }

        double baseDuration = BaseDurationSeconds(grapple);
        if (double.IsNaN(baseDuration))
        {
            return;
        }

        double ownExtra = EffectiveOwnExtraSeconds();
        bool rulerFix = loaderRulerFixEnabled ?? false;
        bool rulerPresent = loaderRulerPresent;
        double deadline = CurrentFlyDeadlineSeconds(baseDuration);
        float fixedAge = (float)fixedAgeProperty.GetValue(state);

        if (fixedAge < deadline)
        {
            float duration = (float)durationField.GetValue(state);
            float floor = fixedAge + 0.05f;
            if (duration < floor)
            {
                durationField.SetValue(state, floor);
            }

            return;
        }

        var watch = flights.GetOrCreateValue(state);
        if (watch.Forced)
        {
            return;
        }

        var outer = outerField.GetValue(state) as EntityStateMachine;
        if (outer == null || outer.state != state)
        {
            return;
        }

        watch.Forced = true;
        outer.SetNextState((EntityState)Activator.CreateInstance(returnStateType));
        Diag?.Info(
            $"LoaderFlyTimeout: FlyState deadline reached after {fixedAge:F3}s " +
            $"(base={baseDuration:F3}s ownExtra={ownExtra:F3}s deadline={deadline:F3}s " +
            $"stuckSyncGrace={PredictionMath.StuckSyncGraceSeconds(CurrentOneWayLatencySeconds()):F3}s " +
            $"rulerPresent={rulerPresent} rulerFix={(rulerFix ? "on" : "off")}, " +
            "hook never stuck locally) -> ReturnState.");
    }

    private static void FireHookFixedUpdatePostfix(FireHook __instance)
    {
        var self = instance;
        if (self == null || !self.ShouldRun())
        {
            return;
        }

        self.Guard(() => self.WatchFireHook(__instance));
    }

    private void WatchFireHook(FireHook state)
    {
        // Remote players' FireHook states are replicated here too; only own them locally.
        if (state.outer == null || !Util.HasEffectiveAuthority(state.outer.networkIdentity))
        {
            return;
        }

        var watch = hooks.GetOrCreateValue(state);
        if (!watch.Started)
        {
            watch.Started = true;
            watch.EnterTime = Time.time;
            watch.StateEnterTime = watch.EnterTime;
            watch.ReleasedTime = watch.EnterTime;
            // FireHook only starts on a press, so the first observed tick is a held key. If it
            // was released before the first postfix, the next tick records that release.
            watch.KeyHeld = true;
        }

        var hook = (GameObject)hookInstanceField.GetValue(state);
        // Visual-only prediction hooks must never drive the fallback: only the authority hook
        // (isPrediction == false) counts as the real hook the player is attached to.
        if (hook != null && hook.GetComponent<ProjectileController>() is { isPrediction: true })
        {
            hook = null;
        }

        if (hook != null)
        {
            if (!watch.SawHook)
            {
                watch.SawHook = true;
                watch.HookSeenTime = Time.time;
                Diag?.Info(
                    $"{Tag}: FireHook got hookInstance after {(watch.HookSeenTime - watch.EnterTime):F2}s " +
                    $"(U={(Latency != null && Latency.TryGetOneWayLatency(out var hookU) ? hookU * 1000.0 : 0.0):F0}ms).");
            }

            if (watch.HookObject != hook)
            {
                watch.HookObject = hook;
                // The projectile owns its ESM ([RequireComponent], ProjectileGrappleController.cs:9);
                // FlyState/ReturnState run there. The customName=="Hook" ESM lives on the owner body
                // (:41-49) and runs FireHook, so searching the hook object for it always returned null.
                watch.HookMachine = hook.GetComponent<EntityStateMachine>();
                watch.HookStateName = null;
                watch.StateEnterTime = Time.time;
            }

            string stateName = watch.HookMachine?.state?.GetType().Name;
            if (stateName != null && stateName != watch.HookStateName)
            {
                Diag?.Info(
                    $"{Tag}: hook state {watch.HookStateName ?? "none"} -> {stateName} " +
                    $"after {(Time.time - watch.EnterTime):F2}s from FireHook enter " +
                    $"({(Time.time - watch.HookSeenTime):F2}s from hookInstance).");
                watch.HookStateName = stateName;
                watch.StateEnterTime = Time.time;
            }
        }
        else if (watch.SawHook && !watch.HookGone)
        {
            watch.HookGone = true;
            Diag?.Info(
                $"{Tag}: hook destroyed after {(Time.time - watch.EnterTime):F2}s from FireHook enter, " +
                $"{(Time.time - watch.HookSeenTime):F2}s after hookInstance.");
        }

        bool keyHeld = state.IsKeyDownAuthority();
        if (watch.KeyHeld && !keyHeld)
        {
            watch.ReleasedTime = Time.time;
        }
        watch.KeyHeld = keyHeld;

        if (watch.Forced || state.outer == null || state.outer.state != state ||
            state.outer.HasPendingState() ||
            !Util.HasEffectiveAuthority(state.outer.networkIdentity))
        {
            return;
        }

        double elapsed = Time.time - watch.EnterTime;
        double timeout = PredictionMath.StuckTimeout(
            TimeoutRttSeconds(),
            LoaderHookFallbackMaxSeconds != null ? LoaderHookFallbackMaxSeconds() : 3f);
        if (elapsed < timeout)
        {
            return;
        }

        string hookStateName = watch.HookMachine?.state?.GetType().Name ?? "none";
        bool stuck = false;
        bool flyWithinDeadline = false;
        if (hook != null)
        {
            var stick = hook.GetComponent<ProjectileStickOnImpact>();
            stuck = stick != null && stick.stuck;
            // A long-range hook may legitimately still be flying; protect it through its own
            // deadline (which now includes the stuck-sync grace) so a pending hit is not cut.
            flyWithinDeadline = FlyStillWithinDeadline(hook, watch.HookMachine);
        }

        // A long hold can put FireHook past the fallback timeout while a healthy ReturnState has
        // only just started; give that return (or a just-released grip) its own full window.
        if (PredictionMath.ReturnFallbackWithinGrace(
                hook != null, stuck, keyHeld, hookStateName,
                Time.time - watch.StateEnterTime, Time.time - watch.ReleasedTime, timeout))
        {
            return;
        }

        string reason = PredictionMath.HookFallbackReason(
            hook != null, watch.SawHook, watch.HookGone, stuck,
            keyHeld, hookStateName, flyWithinDeadline);

        if (reason == null)
        {
            return;
        }

        watch.Forced = true;
        state.outer.SetNextStateToMain();
        Diag?.Warning(
            $"{Tag}: owner fallback -> Main after {elapsed:F2}s (timeout {timeout:F2}s, reason={reason}, " +
            $"hookInstance={(hook != null ? "yes" : "no")}, hookState={hookStateName}, stuck={stuck}, " +
            $"keyDown={keyHeld}, " +
            $"U={(Latency != null && Latency.TryGetOneWayLatency(out var u) ? u * 1000.0 : 0.0):F0}ms).");
    }

    /// <summary>True when the hook's own FlyState has not yet reached our computed deadline.</summary>
    private bool FlyStillWithinDeadline(GameObject hook, EntityStateMachine hookMachine)
    {
        var flyState = hookMachine != null ? hookMachine.state : null;
        if (flyState == null || flyState.GetType() != flyStateType)
        {
            return false;
        }

        var grapple = hook.GetComponent<ProjectileGrappleController>();
        double baseDuration = grapple != null ? BaseDurationSeconds(grapple) : double.NaN;
        if (double.IsNaN(baseDuration))
        {
            return false;
        }

        double deadline = CurrentFlyDeadlineSeconds(baseDuration);
        float flyAge = (float)fixedAgeProperty.GetValue(flyState);
        return flyAge < deadline;
    }

    /// <summary>FlyState deadline shared by the prefix, the postfix and the owner fallback.</summary>
    private double CurrentFlyDeadlineSeconds(double baseDuration)
        => PredictionMath.FlyDeadlineWithStuckGrace(
            baseDuration, EffectiveOwnExtraSeconds(), RulerEquivalentEnabled(),
            loaderRulerFixEnabled ?? false, loaderRulerPresent, CurrentOneWayLatencySeconds());

    private double CurrentOneWayLatencySeconds()
        => Latency != null && Latency.TryGetOneWayLatency(out var oneWay) ? oneWay : 0.0;

    private static void FireHookOnExitPostfix(FireHook __instance)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        try
        {
            if (self.hooks.TryGetValue(__instance, out var watch))
            {
                self.hooks.Remove(__instance);
                if (self.ShouldRun())
                {
                    self.Diag?.Info(
                        $"{Tag}: FireHook exit after {(Time.time - watch.EnterTime):F2}s " +
                        $"(hookSeen={watch.SawHook}, hookGone={watch.HookGone}, forced={watch.Forced}, " +
                        $"lastHookState={watch.HookStateName ?? "none"}).");
                }
            }
        }
        catch (Exception exception)
        {
            // Cleanup/logging must never break the skill's OnExit.
            self.Diag?.Warning($"{Tag}: FireHook exit logging skipped ({exception.GetType().Name}).");
        }
    }

    /// <summary>Ruler 1.1.1 doubles the runtime duration; only enable its model when its toggle is on.</summary>
    private bool DetectLoaderRulerFix()
    {
        double now = Time.time;
        if (loaderRulerFixEnabled.HasValue && now - lastRulerCheckTime < 0.5)
        {
            return loaderRulerFixEnabled.Value;
        }

        lastRulerCheckTime = now;
        bool previouslyKnown = loaderRulerFixEnabled.HasValue;
        bool previousValue = loaderRulerFixEnabled ?? false;
        bool present = false;
        bool enabled = false;
        try
        {
            if (Chainloader.PluginInfos.TryGetValue(LoaderRulerGuid, out var info))
            {
                present = true;
                if (info?.Instance != null)
                {
                    var config = info.Instance.Config;
                    var entry = config?[LoaderRulerSection, LoaderRulerKey];
                    // Plugin present but the key is missing (older build): its documented default is on.
                    enabled = entry == null || (entry.BoxedValue is bool value && value);
                }
            }
        }
        catch (Exception exception)
        {
            enabled = false;
            Diag?.Warning(
                $"{Tag}: LoaderRuler detection failed ({exception.GetType().Name}: {exception.Message}); " +
                "assuming its fix is off.");
        }
        loaderRulerPresent = present;
        if (!previouslyKnown || previousValue != enabled)
        {
            if (present)
            {
                Diag?.Info(
                    $"{Tag}: LoaderRuler detected; '{LoaderRulerKey}'={(enabled ? "on" : "off")}; " +
                    $"our ruler-equivalent compensation={(RulerEquivalentEnabled() ? "on" : "off")}, " +
                    (enabled
                        ? "the same deadline is enforced once (its +dt/2 is not stacked)."
                        : "its off state is respected: no doubling is added."));
            }
            else
            {
                Diag?.Info(
                    $"{Tag}: LoaderRuler not detected; ruler-equivalent compensation=" +
                    $"{(RulerEquivalentEnabled() ? "on" : "off")}, own extra={EffectiveOwnExtraSeconds():F3}s.");
            }
        }

        loaderRulerFixEnabled = enabled;
        return enabled;
    }

    /// <summary>Default-on equivalent of LoaderRuler's client-side timeout doubling.</summary>
    private bool RulerEquivalentEnabled() => ProvideRulerEquivalent == null || ProvideRulerEquivalent();

    private double EffectiveOwnExtraSeconds()
    {
        double margin = FlyTimeoutMarginSeconds != null ? FlyTimeoutMarginSeconds() : 0.0;
        double cap = FlyTimeoutExtraMaxSeconds != null
            ? FlyTimeoutExtraMaxSeconds()
            : PredictionMath.MaxFlyTimeoutExtraSeconds;
        return Math.Min(Math.Max(0.0, margin), Math.Min(Math.Max(0.0, cap), PredictionMath.MaxFlyTimeoutExtraSeconds));
    }

    private static double BaseDurationSeconds(ProjectileGrappleController grapple)
    {
        var simple = grapple.GetComponent<ProjectileSimple>();
        if (simple == null || simple.desiredForwardSpeed <= 0f)
        {
            return double.NaN;
        }

        return grapple.maxTravelDistance / simple.desiredForwardSpeed;
    }

    private double TimeoutRttSeconds()
        => Latency != null && Latency.TryGetTimeoutRoundTrip(out var rtt) ? rtt : 0.0;
}
