// 票07 EnforcerFeedback：冲刺即时反馈 + 卡住兜底 + 到达顺序诊断。
//
// 反编译核对（F:/ror2-decomp/RoR2src）：
// - EntityStates.Enforcer/BaseShieldDashPrepCharge.cs:186-206 FixedUpdate：:189 `if (base.isAuthority)`，
//   :191-195 松键 `BeginDash()`（判定全在本机）；:227-232 BeginDash → ProxyShieldDashHeavy；
//   ShieldDashPrepChargeLight.cs:24-29 轻击 → ProxyShieldDashLight。两者都走 ProxyShieldDash<T>。
// - EntityStates.Enforcer/ProxyShieldDash.cs:128 OnEnter：:164 `referencerToVehicle.ActivateVehicle(...)`；
//   :166-172 只有 NetworkServer.active 才创建载具状态 → 客户端要等 msg48 的载具状态（T+RTT）。
//   反馈本体在同文件 :296 `protected virtual void OnVehicleEnteredState()`（动画/音效/特效），
//   由 :254-259 FixedUpdate 在第一次观察到 `vehicleEnteredState`（vehicleState is T）时调用一次。
// - 卡住根因：:260-269 只有当 `vehicleEnteredState` 已经为真、载具状态又离开 T 时才回 Main；
//   载具状态从未到达时客户端只能靠 :229-252 的 minimumInterruptDuration 后按键自救。
//   兜底复用同一条退出路径 `outer.SetNextStateToMain()`（:268），不新建状态。
// - 到达顺序：msg48 由 RoR2/NetworkStateMachine.cs:79 HandleSetEntityState 处理 → EntityStateMachine.SetState
//   （RoR2/EntityStateMachine.cs:174-193）；座位通过 VehicleSeat.passengerBodyObject SyncVar
//   （RoR2/VehicleSeat.cs:216，hook :408 SetPassenger）到达。两处各打一条带时间戳/序号的低频日志。
// - 卡动画根因（真实房间日志证实）：早期反馈在 ProxyShieldDash.OnVehicleEnteredState 里播了
//   "FullBody, Override"=ShieldTackleLoop；原版只由 VehicleShieldDashExit.PlayExitAnimation
//   （VehicleShieldDashExit.cs:213-230，:37 hasPlayedExitAnimation）在载具退场时复位。兜底/幽灵失败/
//   Proxy 提前退 Main 时载具可能尚未收到 Exit（日志里 arrival 晚于 fallback），于是图层没人复位。
//   本补丁在 EntityStateMachine.SetState 的 prefix 观察“Proxy -> 非 Proxy”转换，幂等补播
//   ShieldTackleExit；该清理不经过 ShouldRun/Guard，配置中途关闭或熔断后仍会执行。
// - ProxyShieldDash.OnExit（:337-395）已负责 DashBodyVFX/喷口/护盾/相机/turnSpeed；兜底走原版
//   SetNextStateToMain，因此这些清理照常触发，本补丁不重复处理。
// - 实测 RTT：OnBodyStateEnter 记录松键时刻，VehicleSeat.SetPassenger 到达时（本机权威乘客）
//   FIFO 匹配出往返样本，Median 窗口在 MeasuredRttEstimator；Enforcer 专用 LatencyEstimator
//   取 min(游戏上报, 实测)，无样本时按 InitialLatencyCapSeconds 封顶。
// 仅 Remote Client 生效；载具状态到达时用“早期反馈标记”去重，不重复播放。
// 实现注意：OnEnter/FixedUpdate/OnVehicleEnteredState 都继承自泛型基类 ProxyShieldDash<T>，
// 两个具体子类解析出的 MethodInfo 元数据相同但属于不同构造泛型；Harmony 对同一方法重复 Patch
// 会叠加执行（PatchInfo.Add 不去重）。因此这里只 Patch 非泛型的 EntityStateMachine.SetState /
// FixedUpdate（每个对象一个），用状态类型分派，绝不重复挂泛型基类方法。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EntityStates;
using EntityStates.Enforcer;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace RoR2LatencyMod;

public sealed class EnforcerFeedbackPatch : SkillPatch
{
    private const string Tag = "EnforcerFeedback";

    private static EnforcerFeedbackPatch instance;
    private static int arrivalSequence;

    /// <summary>Body dash state --&gt; the vehicle state its generic parameter T accepts.</summary>
    private static readonly Dictionary<Type, Type> BodyToVehicleState = new Dictionary<Type, Type>
    {
        { typeof(ProxyShieldDashHeavy), typeof(VehicleShieldDashHeavy) },
        { typeof(ProxyShieldDashLight), typeof(VehicleShieldDashLight) },
    };

    private readonly ConditionalWeakTable<EntityState, StateTracking> tracking =
        new ConditionalWeakTable<EntityState, StateTracking>();

    private readonly Dictionary<Type, MethodInfo> feedbackByType = new Dictionary<Type, MethodInfo>();
    private readonly Dictionary<Type, FieldInfo> vehicleEnteredByType = new Dictionary<Type, FieldInfo>();
    private readonly Dictionary<Type, FieldInfo> referencerByType = new Dictionary<Type, FieldInfo>();
    private readonly HashSet<Type> vehicleDashStateTypes = new HashSet<Type>();

    private static readonly int ShieldTackleLoopStateHash = Animator.StringToHash("ShieldTackleLoop");
    private static readonly int ShieldTackleExitStateHash = Animator.StringToHash("ShieldTackleExit");
    private const string FullBodyOverrideLayer = "FullBody, Override";
    private static readonly FieldInfo exitPassengerBodyField = typeof(VehicleShieldDashExit).GetField(
        "passengerBody", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo controllerVehicleProperty = typeof(EnforcerVehicleDashController)
        .GetProperty("currentVehicle", BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class StateTracking
    {
        public CharacterBody Body;
        public double EnterTime;
        public double TimeoutSeconds;
        public double TimeoutRtt;
        public double EarlyFeedbackTime = double.NegativeInfinity;
        public bool FallbackFired;
        public bool VanillaExitSeen;
        public bool AnimationResetDone;
    }

    public EnforcerFeedbackPatch(
        Func<bool> isEnabled,
        Func<float> feedbackFallbackSeconds,
        Func<float> feedbackFallbackMaxSeconds,
        DiagLog diag = null,
        LatencyEstimator latency = null)
        : base("EnforcerFeedback", PatchSide.Client, isEnabled, diag, latency)
    {
        FeedbackFallbackSeconds = feedbackFallbackSeconds;
        FeedbackFallbackMaxSeconds = feedbackFallbackMaxSeconds;
    }

    /// <summary>0 = compute via PredictionMath.StuckTimeout (max(3×RTT, 1 s), capped); &gt;0 = fixed seconds.</summary>
    public Func<float> FeedbackFallbackSeconds { get; }

    /// <summary>Ceiling for the automatic (and GhostDash) timeout when the fixed one is 0.</summary>
    public Func<float> FeedbackFallbackMaxSeconds { get; }

    protected override void Install()
    {
        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        foreach (var pair in BodyToVehicleState)
        {
            var type = pair.Key;
            var feedback = AccessTools.Method(type, "OnVehicleEnteredState", Type.EmptyTypes);
            var entered = FindField(type, "vehicleEnteredState");
            var referencer = FindField(type, "referencerToVehicle");
            if (feedback == null || entered == null || referencer == null)
            {
                throw new MissingMemberException(
                    $"{type.FullName} members missing: OnVehicleEnteredState={feedback != null}, " +
                    $"vehicleEnteredState={entered != null}, referencerToVehicle={referencer != null}");
            }

            feedbackByType[type] = feedback;
            vehicleEnteredByType[type] = entered;
            referencerByType[type] = referencer;
            vehicleDashStateTypes.Add(pair.Value);
        }

        var setState = AccessTools.Method(typeof(EntityStateMachine), "SetState", new[] { typeof(EntityState) });
        var fixedUpdate = AccessTools.Method(typeof(EntityStateMachine), "FixedUpdate", Type.EmptyTypes);
        var setPassenger = AccessTools.Method(typeof(VehicleSeat), "SetPassenger", new[] { typeof(GameObject) });
        if (setState == null || fixedUpdate == null || setPassenger == null)
        {
            throw new MissingMemberException(
                $"Hooks missing: EntityStateMachine.SetState={setState != null}, " +
                $"EntityStateMachine.FixedUpdate={fixedUpdate != null}, " +
                $"VehicleSeat.SetPassenger={setPassenger != null}");
        }

        harmony.Patch(
            setState,
            prefix: new HarmonyMethod(typeof(EnforcerFeedbackPatch), nameof(VehicleStateArrivalPrefix)),
            postfix: new HarmonyMethod(typeof(EnforcerFeedbackPatch), nameof(BodyStateEnterPostfix)));
        harmony.Patch(
            fixedUpdate,
            prefix: new HarmonyMethod(typeof(EnforcerFeedbackPatch), nameof(SuppressDuplicateFeedbackPrefix)),
            postfix: new HarmonyMethod(typeof(EnforcerFeedbackPatch), nameof(MachineFixedUpdatePostfix)));
        harmony.Patch(setPassenger, postfix: new HarmonyMethod(typeof(EnforcerFeedbackPatch), nameof(SeatPassengerPostfix)));

        instance = this;
        Diag?.Info($"{Tag}: installed (early local feedback, stuck fallback, arrival-order logging).");
    }

    private static void BodyStateEnterPostfix(EntityStateMachine __instance)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        // Cheap gate first: this postfix runs for every EntityStateMachine.SetState in the game.
        var state = __instance.state;
        if (state == null || !BodyToVehicleState.ContainsKey(state.GetType()))
        {
            return;
        }

        self.Guard(() => self.OnBodyStateEnter(state));
    }

    private static void SuppressDuplicateFeedbackPrefix(EntityStateMachine __instance)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        var state = __instance.state;
        if (!IsBodyDashState(state))
        {
            return;
        }

        self.Guard(() => self.SuppressDuplicateFeedback(state));
    }

    private static void MachineFixedUpdatePostfix(EntityStateMachine __instance)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        var state = __instance.state;
        if (!IsBodyDashState(state))
        {
            return;
        }

        self.Guard(() => self.OnMachineFixedUpdate(__instance, state));
    }

    /// <summary>Cheap gate first: EntityStateMachine.FixedUpdate runs on every entity every tick.</summary>
    private static bool IsBodyDashState(EntityState state)
        => state != null && BodyToVehicleState.ContainsKey(state.GetType());

    private static void VehicleStateArrivalPrefix(object __instance, EntityState newState)
    {
        var self = instance;
        if (self == null || newState == null)
        {
            return;
        }

        var machine = (EntityStateMachine)__instance;
        // SetState runs for every entity in the game; only these two transitions allocate/log.
        if (self.vehicleDashStateTypes.Contains(newState.GetType()) || newState is VehicleShieldDashExit)
        {
            self.Guard(() =>
            {
                self.LogVehicleStateArrival(machine, newState);
                self.MarkVanillaExitIfNeeded(machine, newState);
            });
        }

        // Runs even when the config was switched off or the patch tripped: an early feedback
        // already played is exactly the state we must not leave looping.
        self.SafeRestoreAfterStateExit(machine, newState);
    }

    private static void SeatPassengerPostfix(object __instance, GameObject newPassengerBodyObject)
    {
        var self = instance;
        if (self == null || newPassengerBodyObject == null)
        {
            return;
        }

        self.Guard(() => self.LogSeatArrival((VehicleSeat)__instance, newPassengerBodyObject));
    }

    private void OnBodyStateEnter(EntityState state)
    {
        if (state == null || !ShouldRun() || !IsLocalAuthority(state))
        {
            return;
        }

        var type = state.GetType();
        if (!BodyToVehicleState.ContainsKey(type))
        {
            return;
        }

        var now = Time.time;
        if (!tracking.TryGetValue(state, out var entry))
        {
            entry = new StateTracking();
            tracking.Add(state, entry);
        }

        entry.EnterTime = now;
        entry.Body = state.outer != null ? state.outer.commonComponents.characterBody : null;
        // One pending round trip per local dash entry; the next seat arrival consumes it.
        Latency?.Measured?.ObserveEnter(now);
        // Capture once: a sample that lands mid-dash must not shrink this dash's timeout
        // below the elapsed time and force an immediate fallback.
        entry.TimeoutRtt = TimeoutRttSeconds();
        entry.TimeoutSeconds = EffectiveFallbackSeconds(entry.TimeoutRtt);

        if (!feedbackByType.TryGetValue(type, out var feedback))
        {
            return;
        }

        // Play the vanilla feedback body now instead of waiting for the vehicle state (T+RTT).
        // Flag first: if feedback throws halfway, the loop may already be playing and the
        // exit cleanup must still know about it.
        entry.EarlyFeedbackTime = now;
        feedback.Invoke(state, null);

        var oneWay = OneWaySeconds();
        Diag?.Throttled(
            $"{Tag}.enter.{type.Name}",
            $"{Tag}: early local feedback on enter ({type.Name}) U={oneWay:F3}s {Latency?.Describe()}");
    }

    /// <summary>
    /// Runs before the body state's FixedUpdate. Once the early feedback has played, mark the
    /// vehicle state as observed so vanilla takes the exact same path minus only the feedback
    /// call; the field must stay true so the later "vehicle state left T" exit still works.
    /// </summary>
    private void SuppressDuplicateFeedback(EntityState state)
    {
        if (state == null || !ShouldRun())
        {
            return;
        }

        var type = state.GetType();
        if (!BodyToVehicleState.TryGetValue(type, out var expectedVehicleState) || !IsLocalAuthority(state))
        {
            return;
        }

        if (!tracking.TryGetValue(state, out var entry) || double.IsNegativeInfinity(entry.EarlyFeedbackTime))
        {
            return;
        }

        if (!vehicleEnteredByType.TryGetValue(type, out var enteredField) || (bool)enteredField.GetValue(state))
        {
            return;
        }

        if (!referencerByType.TryGetValue(type, out var referencerField))
        {
            return;
        }

        var referencer = referencerField.GetValue(state) as EnforcerVehicleDashController;
        var vehicleState = referencer != null ? referencer.GetVehicleState() : null;
        if (vehicleState == null || !expectedVehicleState.IsInstanceOfType(vehicleState))
        {
            return;
        }

        enteredField.SetValue(state, true);
        Diag?.Info(
            $"{Tag}: early feedback already played; vanilla OnVehicleEnteredState suppressed when " +
            $"{vehicleState.GetType().Name} arrived for {type.Name}.");
    }

    private void OnMachineFixedUpdate(EntityStateMachine machine, EntityState state)
    {
        var type = state.GetType();
        if (!ShouldRun() || !IsLocalAuthority(state))
        {
            return;
        }

        if (!tracking.TryGetValue(state, out var entry) || entry.FallbackFired)
        {
            return;
        }

        if (!vehicleEnteredByType.TryGetValue(type, out var enteredField) ||
            (bool)enteredField.GetValue(state))
        {
            return;
        }

        if (machine.HasPendingState())
        {
            return;
        }

        var elapsed = Time.time - entry.EnterTime;
        // P10: entry.TimeoutSeconds was cached at dash entry. When the RTT spikes mid-dash
        // (log: 0.352 s -> 0.966 s at check time) the cached 1.06 s fired 0.35 s before the
        // late vehicle arrived. Re-derive from the current RTT, never shrinking; a fixed
        // override stays fixed.
        var timeout = entry.TimeoutSeconds;
        if (!(FeedbackFallbackSeconds?.Invoke() > 0f))
        {
            timeout = PredictionMath.ExtendStuckTimeout(
                timeout,
                TimeoutRttSeconds(),
                FeedbackFallbackMaxSeconds?.Invoke() ?? (float)PredictionMath.MaxStuckTimeoutSeconds);
        }

        if (elapsed < timeout)
        {
            return;
        }

        entry.FallbackFired = true;
        TryRestoreExitAnimation(entry, "stuck fallback -> Main");
        machine.SetNextStateToMain();
        var oneWay = OneWaySeconds();
        Diag?.Info(
            $"{Tag}: stuck fallback -> Main after {elapsed:F2}s (timeout {timeout:F2}s, " +
            $"cached={entry.TimeoutSeconds:F2}s, timeoutRTT={entry.TimeoutRtt:F3}s, U={oneWay:F3}s {Latency?.Describe()}); " +
            $"vehicleEnteredState=false, vehicleState={VehicleStateName(state)}, pending=false; " +
            "reason=vehicle state never observed");
    }

    private void LogVehicleStateArrival(EntityStateMachine machine, EntityState newState)
    {
        if (!ShouldRun() || !vehicleDashStateTypes.Contains(newState.GetType()))
        {
            return;
        }

        var sequence = ++arrivalSequence;
        var oneWay = OneWaySeconds();
        var passenger = machine.GetComponent<VehicleSeat>()?.currentPassengerBody;
        var late = "";
        if (passenger != null && passenger.hasEffectiveAuthority)
        {
            var bodyMachine = EntityStateMachine.FindByCustomName(passenger.gameObject, "Body");
            var bodyState = bodyMachine != null ? bodyMachine.state : null;
            if (bodyState == null || !BodyToVehicleState.ContainsKey(bodyState.GetType()))
            {
                // The local proxy already fell back; vanilla seat drags and the later
                // VehicleShieldDashExit own the presentation. Logged so the next test can
                // judge the residual seat-snap jump.
                late = $" late=local bodyState={bodyState?.GetType().Name ?? "none"}";
            }
        }

        Diag?.Info(
            $"{Tag}: arrival #{sequence} t={Time.time:F3} vehicleState={newState.GetType().Name} " +
            $"object={machine.gameObject.name} U={oneWay:F3}s {Latency?.Describe()}{late}");
    }

    private void LogSeatArrival(VehicleSeat seat, GameObject passenger)
    {
        if (!ShouldRun() || passenger.GetComponent<EnforcerVehicleDashController>() == null)
        {
            return;
        }

        var sequence = ++arrivalSequence;
        var oneWay = OneWaySeconds();
        var body = passenger.GetComponent<CharacterBody>();
        var sampleText = "";
        if (body != null && body.hasEffectiveAuthority && Latency?.Measured != null)
        {
            if (Latency.Measured.TryConsumeArrival(Time.time, out var sample))
            {
                Latency.Measured.TryGetMedian(out var median);
                sampleText = $" measuredSample={sample:F3}s median={median:F3}s " +
                             $"n={Latency.Measured.SampleCount}";
            }
        }

        Diag?.Info(
            $"{Tag}: arrival #{sequence} t={Time.time:F3} seatPassenger={Util.GetBestBodyName(passenger)} " +
            $"vehicle={seat.gameObject.name} U={oneWay:F3}s {Latency?.Describe()}{sampleText}");
    }

    private string VehicleStateName(EntityState state)
    {
        if (!referencerByType.TryGetValue(state.GetType(), out var field))
        {
            return "unknown";
        }

        var referencer = field.GetValue(state) as EnforcerVehicleDashController;
        return referencer?.GetVehicleState()?.GetType().Name ?? "none";
    }

    private double EffectiveFallbackSeconds(double rtt)
    {
        var configured = FeedbackFallbackSeconds?.Invoke() ?? 0f;
        if (configured > 0f)
        {
            return configured;
        }

        var maxSeconds = FeedbackFallbackMaxSeconds?.Invoke() ?? (float)PredictionMath.MaxStuckTimeoutSeconds;
        return PredictionMath.StuckTimeout(rtt, maxSeconds);
    }

    /// <summary>
    /// Fallback RTT uses the larger of game and measured (a spike must not fire before the
    /// late arrival); the 3x RTT / 1 s floor is applied by PredictionMath.StuckTimeout.
    /// </summary>
    private double TimeoutRttSeconds()
        => Latency != null && Latency.TryGetTimeoutRoundTrip(out var rtt) ? rtt : 0.0;

    /// <summary>
    /// Vanilla VehicleShieldDashExit already plays ShieldTackleExit exactly once. Remember it
    /// so the local reset does not rewind the same animation when the body proxy leaves.
    /// </summary>
    private void MarkVanillaExitIfNeeded(EntityStateMachine vehicleMachine, EntityState newState)
    {
        if (!(newState is VehicleShieldDashExit))
        {
            return;
        }

        var passenger = exitPassengerBodyField?.GetValue(newState) as CharacterBody;
        if (passenger == null || !passenger.hasEffectiveAuthority)
        {
            return;
        }

        var bodyMachine = EntityStateMachine.FindByCustomName(passenger.gameObject, "Body");
        var bodyState = bodyMachine != null ? bodyMachine.state : null;
        if (bodyState == null || !tracking.TryGetValue(bodyState, out var entry))
        {
            return;
        }

        // Guard against a late exit from the previous dash: only the vehicle this proxy is
        // actually riding may suppress the local reset.
        var controller = passenger.GetComponent<EnforcerVehicleDashController>();
        var currentVehicle = controller != null && controllerVehicleProperty != null
            ? controllerVehicleProperty.GetValue(controller) as GameObject
            : null;
        if (currentVehicle == null || vehicleMachine == null || currentVehicle != vehicleMachine.gameObject)
        {
            return;
        }

        entry.VanillaExitSeen = true;
        Diag?.Info(
            $"{Tag}: vanilla exit animation seen for {bodyState.GetType().Name}; " +
            "local ShieldTackleExit reset will not run again.");
    }

    /// <summary>
    /// Every body-side exit (fallback, vanilla early Main, hit stun, death, config off,
    /// tripped patch) passes through EntityStateMachine.SetState. This deliberately skips
    /// ShouldRun/Guard: cleanup must still happen after a trip or a mid-run config change.
    /// </summary>
    private void SafeRestoreAfterStateExit(EntityStateMachine machine, EntityState newState)
    {
        try
        {
            var oldState = machine.state;
            if (oldState == null || !BodyToVehicleState.ContainsKey(oldState.GetType()))
            {
                return;
            }

            if (BodyToVehicleState.ContainsKey(newState.GetType()))
            {
                return; // still a dash proxy: the loop is the correct animation
            }

            if (!tracking.TryGetValue(oldState, out var entry))
            {
                return;
            }

            tracking.Remove(oldState);
            if (entry.AnimationResetDone || entry.VanillaExitSeen ||
                double.IsNegativeInfinity(entry.EarlyFeedbackTime))
            {
                return;
            }

            TryRestoreExitAnimation(entry, $"proxy state exited -> {newState.GetType().Name}");
        }
        catch (Exception exception)
        {
            // A failed cosmetic reset must never break the state machine transition.
            Diag?.Warning(
                $"{Tag}: exit animation reset skipped ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    /// <summary>Idempotent local equivalent of VehicleShieldDashExit.PlayExitAnimation.</summary>
    private void TryRestoreExitAnimation(StateTracking entry, string reason)
    {
        if (entry == null || entry.AnimationResetDone ||
            double.IsNegativeInfinity(entry.EarlyFeedbackTime))
        {
            return;
        }

        if (entry.VanillaExitSeen)
        {
            entry.AnimationResetDone = true;
            return;
        }

        var animator = entry.Body != null ? entry.Body.modelLocator?.modelAnimator : null;
        if (animator == null)
        {
            Diag?.Warning($"{Tag}: exit animation reset skipped: model animator unavailable ({reason}).");
            return;
        }

        var layerIndex = animator.GetLayerIndex(FullBodyOverrideLayer);
        if (layerIndex < 0)
        {
            Diag?.Warning($"{Tag}: exit animation reset skipped: layer '{FullBodyOverrideLayer}' missing.");
            return;
        }

        var before = animator.GetCurrentAnimatorStateInfo(layerIndex).shortNameHash;
        animator.speed = 1f;
        animator.Update(0f);
        animator.PlayInFixedTime(ShieldTackleExitStateHash, layerIndex, 0f);
        entry.AnimationResetDone = true;
        // P9: the local early end already played this pose; its vanilla exit arrival must not replay it.
        EnforcerLocalEndPatch.NotifyExitAnimationPlayed(entry.Body);
        Diag?.Info(
            $"{Tag}: animation reset {FullBodyOverrideLayer}=ShieldTackleExit reason={reason}; " +
            $"before={HashLabel(before)}; duplicates are harmless, vanilla exit may replay it later.");
    }

    private static string HashLabel(int hash)
        => hash == ShieldTackleLoopStateHash ? "ShieldTackleLoop"
            : hash == ShieldTackleExitStateHash ? "ShieldTackleExit"
            : hash.ToString();

    private double OneWaySeconds()
    {
        double oneWay = 0.0;
        Latency?.TryGetOneWayLatency(out oneWay);
        return oneWay;
    }

    private static bool IsLocalAuthority(EntityState state)
    {
        var identity = state.outer != null ? state.outer.networkIdentity : null;
        return identity != null && Util.HasEffectiveAuthority(identity);
    }

    /// <summary>AccessTools.Field does not always reach private fields on the generic base; walk it.</summary>
    private static FieldInfo FindField(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }
}
