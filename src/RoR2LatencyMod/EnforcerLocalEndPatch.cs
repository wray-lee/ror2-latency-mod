// 票P9 EnforcerLocalEnd：轻击/重击幽灵冲刺到位后，本地提前结束冲刺状态并释放操控，
// 消除「本地冲完仍保持冲刺姿态、等服务器 eject」的 0.2–1.1s 尾延迟（默认开）。
//
// 反编译核对（F:/ror2-decomp/RoR2src，只读）：
// - ProxyShieldDash.OnExit 只清理相机/VFX/护盾/turnSpeed；不决定位置。
// - 座位锁：VehicleSeat.cs:482-492 每帧把 passengerState 强制进 Body 机（仅 effective
//   authority 时）；:511-538 UpdatePassengerPosition 每帧清零速度并把乘客钉到 seatPosition。
//   逐帧 suppress 会漏掉 motor/direction/collider/currentVehicle/交互/CNT，恢复与禁用都由
//   OnPassengerEnter/OnPassengerExit 对称完成，所以本地结束直接调用原版
//   SetPassenger(null)（:391-406 → :780-918），一次性释放全部锁。
// - 纯客户端安全：SetPassengerInternal:398 经 NetworkpassengerBodyObject（:264-281）赋值；
//   纯客户端 NetworkServer.localClientActive=false，只改本地字段。Host 上同一路径会重入
//   hook 并 SetSyncVarGameObject 广播 null，因此本补丁硬禁用 Host（另加显式 NetworkServer.active）。
// - handleExitTeleport（:240 默认 true，:889-897 传送到 seatPosition）在本地 eject 期间临时关掉，
//   否则解座瞬间被拽回；OnPassengerExit:870-872 会清零速度，但 :903-906 在座位有 rigidbody 时
//   会再写入退场速度，故 eject 后补清一次。
// - 客户端退出状态 VehicleShieldDashExit：:124-144 OnPassengerExitAuthority 做 :136-138
//   velocity/硬贴、:140 重开 CNT transmit、:142 播退出动画；:192-204 FixedUpdate 在客户端
//   （remoteAuthorityDelay 默认 0）次帧回 Main，:210 OnExit 还会再调 PlayExitAnimation。
//   闩锁命中时用 prefix 跳过整个 authority 方法（自行重开 transmit、清 air-control），并在
//   SetState 前缀（OnEnter 之前）按需置实例字段 hasPlayedExitAnimation，防止退款/重播。
// - 退出动画提前由 EnforcerFeedback 在 Proxy 退出时播放（SafeRestoreAfterStateExit →
//   TryRestoreExitAnimation）；本补丁通过 NotifyExitAnimationPlayed 记录是否已播。
// - 触发时刻取幽灵运动完成（GhostDash 的 MotionDone / 墙停），不用 RTT 预测退出（B）。
//   座位晚到（本地 eject 后 SyncVar 重发乘客）时在座位 UpdatePassengerPosition 前缀再次
//   eject，幂等、不重播动画、不再结束状态。
// - 只改本地状态/位置呈现；不改伤害/命中，不新增网络消息类型；任何异常/阈值异常即熔断回退。

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using EntityStates;
using EntityStates.Enforcer;
using HarmonyLib;
using RoR2;
using RoR2.Networking;
using UnityEngine;
using UnityEngine.Networking;

namespace RoR2LatencyMod;

public sealed class EnforcerLocalEndPatch : SkillPatch
{
    private const string Tag = "EnforcerLocalEnd";

    /// <summary>Extra wait before firing when the ghost never advances (stalled simulation).</summary>
    private const double WatchdogSeconds = 0.15;

    private static EnforcerLocalEndPatch instance;
    private static PropertyInfo motorProperty;
    private static FieldInfo baseVelocityField;
    private static FieldInfo seatPassengerField;
    private static FieldInfo exitPassengerField;
    private static FieldInfo exitAnimationPlayedField;
    private static MethodInfo setPassengerMethod;
    private static MethodInfo exitAuthorityMethod;
    private static PropertyInfo controllerVehicleProperty;
    private static FieldInfo vehicleIdField;

    private readonly ConditionalWeakTable<CharacterMotor, Latch> latches =
        new ConditionalWeakTable<CharacterMotor, Latch>();

    // A rapid second dash replaces the latch; its exit must still be absorbed for the old vehicle.
    private readonly ConditionalWeakTable<CharacterBody, LocalEndHistory> history =
        new ConditionalWeakTable<CharacterBody, LocalEndHistory>();

    private sealed class Latch
    {
        public CharacterBody Body;
        public CharacterMotor Motor;
        public EntityStateMachine BodyMachine;
        public EntityState DashState;
        public GameObject Vehicle;
        public uint VehicleNetId;
        public double StartTime;
        public double MotionSeconds;
        public double Duration;
        public double PredictedTravel;
        public double OneWay;
        public bool Fired;
        public bool AnimPlayed;
        public bool VanillaExitSeen;
        public bool ExitHandled;
        public double ExpireAt;
        public bool EjectFailureLogged;
    }

    private sealed class LocalEndHistory
    {
        public uint VehicleNetId;
        public double FiredAt;
        public double WindowSeconds;
        public bool AnimPlayed;
    }

    public EnforcerLocalEndPatch(Func<bool> isEnabled, DiagLog diag = null, LatencyEstimator latency = null)
        : base("EnforcerLocalEnd", PatchSide.Client, isEnabled, diag, latency)
    {
    }

    protected override void Install()
    {
        motorProperty = typeof(CharacterMotor).GetProperty("Motor");
        if (motorProperty != null)
        {
            // KinematicCharacterMotor.BaseVelocity is a public field, not a property.
            baseVelocityField = motorProperty.PropertyType.GetField("BaseVelocity");
        }

        seatPassengerField = AccessTools.Field(typeof(VehicleSeat), "passengerBodyObject");
        exitPassengerField = AccessTools.Field(typeof(VehicleShieldDashExit), "passengerBody");
        exitAnimationPlayedField = AccessTools.Field(typeof(VehicleShieldDashExit), "hasPlayedExitAnimation");
        setPassengerMethod = AccessTools.Method(
            typeof(VehicleSeat), "SetPassenger", new[] { typeof(GameObject) });
        exitAuthorityMethod = AccessTools.Method(
            typeof(VehicleShieldDashExit), "OnPassengerExitAuthority", new[] { typeof(GameObject) });
        controllerVehicleProperty = typeof(EnforcerVehicleDashController).GetProperty(
            "currentVehicle", BindingFlags.Instance | BindingFlags.NonPublic);
        vehicleIdField = typeof(EnforcerVehicleDashController).GetField("currentVehicleID");

        var setState = AccessTools.Method(
            typeof(EntityStateMachine), "SetState", new[] { typeof(EntityState) });
        var beforeCharacterUpdate = AccessTools.Method(
            typeof(CharacterMotor), "BeforeCharacterUpdate", new[] { typeof(float) });
        var exitAuthority = AccessTools.Method(
            typeof(VehicleShieldDashExit), "OnPassengerExitAuthority", new[] { typeof(GameObject) });
        var forcePassengerState = AccessTools.Method(
            typeof(VehicleSeat), "ForcePassengerState", Type.EmptyTypes);

        if (motorProperty == null || baseVelocityField == null
            || seatPassengerField == null || exitPassengerField == null
            || exitAnimationPlayedField == null || setPassengerMethod == null
            || exitAuthority == null || controllerVehicleProperty == null
            || setState == null || beforeCharacterUpdate == null || forcePassengerState == null)
        {
            throw new MissingMemberException(
                "EnforcerLocalEnd members missing: " +
                $"Motor={motorProperty != null}, BaseVelocity={baseVelocityField != null}, " +
                $"passengerBodyObject={seatPassengerField != null}, passengerBody={exitPassengerField != null}, " +
                $"hasPlayedExitAnimation={exitAnimationPlayedField != null}, SetPassenger={setPassengerMethod != null}, " +
                $"OnPassengerExitAuthority={exitAuthority != null}, currentVehicle={controllerVehicleProperty != null}, " +
                $"SetState={setState != null}, BeforeCharacterUpdate={beforeCharacterUpdate != null}, " +
                $"ForcePassengerState={forcePassengerState != null}");
        }

        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            setState,
            prefix: new HarmonyMethod(typeof(EnforcerLocalEndPatch), nameof(StateSetPrefix)));
        harmony.Patch(
            beforeCharacterUpdate,
            postfix: new HarmonyMethod(typeof(EnforcerLocalEndPatch), nameof(MotorPostfix)));
        harmony.Patch(
            exitAuthority,
            prefix: new HarmonyMethod(typeof(EnforcerLocalEndPatch), nameof(ExitAuthorityPrefix)));
        harmony.Patch(
            forcePassengerState,
            prefix: new HarmonyMethod(typeof(EnforcerLocalEndPatch), nameof(ForcePassengerStatePrefix)));

        instance = this;
        Diag?.Info(
            $"{Tag}: installed (fire at ghost motion end, local eject, idempotent vanilla-exit absorb).");
    }

    /// <summary>Called by GhostDash after every ghost step: arm, fire and expire latches.</summary>
    [HarmonyPriority(Priority.Last)]
    private static void MotorPostfix(CharacterMotor __instance)
    {
        var self = instance;
        if (self is null || __instance is null)
        {
            return;
        }

        self.Guard(() => self.TickMotor(__instance));
    }

    /// <summary>
    /// Called from GhostDash's VehicleSeat.UpdatePassengerPosition prefix. True means local end
    /// owns this seat this frame: skip the vanilla snap, and step the ghost here when the
    /// passenger motor is disabled.
    /// </summary>
    internal static bool TryControlSeat(VehicleSeat seat)
    {
        var self = instance;
        if (self == null)
        {
            return false;
        }

        bool suppress = false;
        self.Guard(() => suppress = self.ControlSeat(seat));
        return suppress;
    }

    /// <summary>True while this body's dash ghost is driven by local end (no vanilla handoff).</summary>
    internal static bool OwnsGhost(CharacterBody body)
    {
        var self = instance;
        if (self == null || body == null)
        {
            return false;
        }

        bool owns = false;
        self.Guard(() => owns = self.OwnsGhostCore(body));
        return owns;
    }

    /// <summary>ExitSmoothing must not re-introduce the hard snap we suppressed.</summary>
    internal static bool SuppressesExitSmoothing(VehicleShieldDashExit exit)
    {
        var self = instance;
        if (self == null || exit == null)
        {
            return false;
        }

        bool suppress = false;
        self.Guard(() => suppress = self.SuppressExitSmoothingCore(exit));
        return suppress;
    }

    /// <summary>Feedback reports a played ShieldTackleExit so the vanilla arrival does not replay it.</summary>
    internal static void NotifyExitAnimationPlayed(CharacterBody body)
    {
        var self = instance;
        if (self == null || body == null)
        {
            return;
        }

        self.Guard(() =>
        {
            var latch = self.FindLatch(body);
            if (latch != null && latch.Fired)
            {
                latch.AnimPlayed = true;
            }

            if (self.history.TryGetValue(body, out var entry)
                && Time.time <= entry.FiredAt + entry.WindowSeconds)
            {
                entry.AnimPlayed = true;
            }
        });
    }

    private void TickMotor(CharacterMotor motor)
    {
        if (!latches.TryGetValue(motor, out var latch))
        {
            TryArm(motor);
            return;
        }

        if (latch.Fired)
        {
            // A new dash may reuse the same motor before the old latch expires.
            if (EnforcerGhostDashPatch.TryGetLocalEndStatus(motor, out var nextStatus)
                && nextStatus.Found && nextStatus.BodyState != latch.DashState)
            {
                Remove(latch, "new dash");
                TryArm(motor);
                return;
            }

            CheckExpiry(latch);
            return;
        }

        if (!ShouldRun())
        {
            Remove(latch, "disabled");
            return;
        }

        if (!Alive(latch.Body))
        {
            Remove(latch, "death");
            return;
        }

        if (latch.VanillaExitSeen)
        {
            Remove(latch, "vanilla exit first");
            return;
        }

        // Seated: VehicleSeat.UpdatePassengerPosition is the only safe place to eject, and
        // its prefix (ControlSeat) already drives the trigger.
        if (latch.Body.currentVehicle != null)
        {
            return;
        }

        if (!EnforcerGhostDashPatch.TryGetLocalEndStatus(motor, out var status))
        {
            Remove(latch, "ghost stopped before local end");
            return;
        }

        ValidateAndFire(latch, status, "motor");
    }

    private Latch TryArm(CharacterMotor motor)
    {
        if (!ShouldRun())
        {
            return null;
        }

        if (!EnforcerGhostDashPatch.TryGetLocalEndStatus(motor, out var status))
        {
            return null;
        }

        if (!status.Found || !status.LeadRecorded || status.Handoff || status.StateExited)
        {
            return null;
        }

        var body = status.Body;
        if (!Alive(body) || !body.hasEffectiveAuthority)
        {
            return null;
        }

        var latch = new Latch
        {
            Body = body,
            Motor = motor,
            BodyMachine = status.BodyMachine,
            DashState = status.BodyState,
            Vehicle = FindVehicle(body),
            VehicleNetId = ReadVehicleNetId(body),
            StartTime = status.StartTime,
            MotionSeconds = status.MotionSeconds,
            Duration = status.Duration,
            PredictedTravel = status.PredictedTravel,
            OneWay = status.OneWay,
        };
        latches.Remove(motor);
        latches.Add(motor, latch);
        Diag?.Info(
            $"{Tag}: latch armed state={status.BodyState.GetType().Name} " +
            $"motion={status.MotionSeconds:F2}s predicted={status.PredictedTravel:F2}m U={status.OneWay:F3}s.");
        return latch;
    }

    /// <summary>
    /// A VehicleShieldDashExit for a latch that has not fired and whose current dash never
    /// saw the vehicle start state is the previous dash's exit on the reused vehicle: state
    /// messages are ordered, so the new dash's start state would have arrived first otherwise.
    /// The history check keeps a first dash whose start/exit arrived in one batch on vanilla.
    /// </summary>
    private bool ExitPredatesCurrentDash(
        Latch latch, GameObject vehicleRoot, out bool previousAnimationPlayed)
    {
        previousAnimationPlayed = false;
        if (latch == null || latch.Fired
            || !HistoryMatches(vehicleRoot, latch.Body, out var historyEntry))
        {
            return false;
        }

        previousAnimationPlayed = historyEntry.AnimPlayed;
        return EnforcerGhostDashPatch.TryGetLocalEndStatus(latch.Motor, out var status)
               && status.Found
               && !status.VehicleStateSeen;
    }

    private bool ExitPredatesCurrentDash(Latch latch, GameObject vehicleRoot)
        => ExitPredatesCurrentDash(latch, vehicleRoot, out _);

    /// <summary>The body machine moved to a new dash proxy state, so the old latch is stale.</summary>
    private static bool SupersededByNewDash(Latch latch)
    {
        if (latch == null || latch.BodyMachine == null)
        {
            return false;
        }

        var state = latch.BodyMachine.state;
        return state != null
               && !ReferenceEquals(state, latch.DashState)
               && EnforcerGhostDashPatch.IsDashProxyState(state);
    }

    private bool ControlSeat(VehicleSeat seat)
    {
        if (!ShouldRun() || seat == null || seat.seatPosition == null)
        {
            return false;
        }

        var passenger = seatPassengerField.GetValue(seat) as GameObject;
        var body = passenger != null ? passenger.GetComponent<CharacterBody>() : null;
        if (body == null || !body.hasEffectiveAuthority)
        {
            return false;
        }

        var motor = body.characterMotor;
        if (motor is null)
        {
            return false;
        }

        if (!latches.TryGetValue(motor, out var latch))
        {
            latch = TryArm(motor);
            if (latch == null)
            {
                return false;
            }
        }
        else if (SupersededByNewDash(latch))
        {
            // A second dash within the old latch window must arm its own latch; otherwise the
            // old fired latch would eject the player at the start of the new dash.
            Remove(latch, "new dash state");
            latch = TryArm(motor);
            if (latch == null)
            {
                return false;
            }
        }

        if (latch.Vehicle == null && latch.VehicleNetId == 0)
        {
            if (body.currentVehicle == seat)
            {
                latch.Vehicle = seat.gameObject;
            }
            else
            {
                var found = FindVehicle(body);
                if (found != null)
                {
                    latch.Vehicle = found;
                }
            }
        }

        RefreshLatchVehicleId(latch);
        if (!VehicleMatches(seat.gameObject, latch))
        {
            Remove(latch, "seat changed");
            return false;
        }

        if (latch.Fired)
        {
            // Late SyncVar re-entry after our eject: eject again without replaying anything.
            bool ejected = EjectLocally(seat, latch);
            if (ejected)
            {
                ClearEjectedVelocity(body);
                EnableAuthorityMotion(body);
            }

            // If the eject failed, let the vanilla seat pin/zero the body instead of skipping it.
            return ejected;
        }

        if (latch.VanillaExitSeen)
        {
            Remove(latch, "vanilla exit first");
            return false;
        }

        if (!Alive(body))
        {
            Remove(latch, "death");
            return false;
        }

        if (seat.disablePassengerMotor)
        {
            if (!EnforcerGhostDashPatch.StepLocalEndGhost(motor, Time.deltaTime, out var seatedStatus))
            {
                Remove(latch, "ghost stopped while seated");
                return false;
            }

            ValidateAndFire(latch, seatedStatus, "seat");
        }
        else if (EnforcerGhostDashPatch.TryGetLocalEndStatus(motor, out var motorStatus))
        {
            // The motor is still running, so its hook already stepped the ghost this frame.
            ValidateAndFire(latch, motorStatus, "seat-motor");
        }
        else
        {
            Remove(latch, "ghost stopped while seated");
            return false;
        }

        return true;
    }

    private void ValidateAndFire(
        Latch latch, EnforcerGhostDashPatch.LocalEndGhostStatus status, string source)
    {
        if (latch.Fired || latch.VanillaExitSeen || !status.Found
            || status.BodyState != latch.DashState || !status.LeadRecorded
            || status.Handoff || status.StateExited)
        {
            return;
        }

        double trigger = PredictionMath.LocalEndTriggerTime(
            status.StartTime, status.MotionSeconds, status.OneWay, status.Duration);
        if (!status.MotionDone && Time.time < trigger + WatchdogSeconds)
        {
            return;
        }

        Fire(latch, status, source);
    }

    private bool Fire(Latch latch, EnforcerGhostDashPatch.LocalEndGhostStatus status, string source)
    {
        if (!ShouldRun())
        {
            Remove(latch, "disabled");
            return false;
        }

        var body = latch.Body;
        if (!Alive(body))
        {
            Remove(latch, "death");
            return false;
        }

        if (!body.hasEffectiveAuthority)
        {
            Remove(latch, "lost authority");
            return false;
        }

        // Host safety: NetworkpassengerBodyObject re-enters the hook and broadcasts null.
        if (NetworkServer.active)
        {
            Remove(latch, "host");
            return false;
        }

        var machine = latch.BodyMachine;
        if (machine == null || machine.state != latch.DashState)
        {
            Remove(latch, "dash state exited");
            return false;
        }

        var seat = body.currentVehicle;
        if (seat != null && !VehicleMatches(seat.gameObject, latch))
        {
            Remove(latch, "current vehicle changed");
            return false;
        }

        // Set before eject: the seat's onPassengerExit may call back into our exit prefix.
        latch.Fired = true;
        latch.PredictedTravel = status.PredictedTravel;
        latch.ExpireAt = Time.time + PredictionMath.LocalEndLatchSeconds(2.0 * latch.OneWay);

        EnforcerGhostDashPatch.StopLocalEndGhost(latch.Motor, "local early end");

        if (seat != null)
        {
            if (!EjectLocally(seat, latch))
            {
                // Undo the latch: the vanilla seat lock/exit path stays authoritative.
                latch.Fired = false;
                latch.ExpireAt = 0.0;
                Remove(latch, "local eject failed; vanilla exit resumes");
                return false;
            }

            ClearEjectedVelocity(body);
        }
        else if (machine.state == latch.DashState && machine.CanInterruptState(InterruptPriority.Any))
        {
            // No seat yet (or the seat vanished first): end the proxy so the pose/skill release
            // even though the server vehicle is still dashing.
            machine.SetNextStateToMain();
        }

        EnableAuthorityMotion(body);
        if (latch.VehicleNetId == 0)
        {
            latch.VehicleNetId = ReadVehicleNetId(body);
        }

        RefreshLatchVehicleId(latch);
        RecordHistory(body, latch);
        Diag?.Info(
            $"{Tag}: local end fired source={source} t={Time.time - latch.StartTime:F2}s " +
            $"motion={latch.MotionSeconds:F2}s predicted={latch.PredictedTravel:F2}m U={latch.OneWay:F3}s " +
            $"seat={(seat != null ? "yes" : "none")} " +
            $"pos=({body.transform.position.x:F2},{body.transform.position.z:F2}).");
        return true;
    }

    private bool EjectLocally(VehicleSeat seat, Latch latch)
    {
        if (NetworkServer.active || setPassengerMethod == null || seat == null
            || latch == null || latch.Body == null)
        {
            return false;
        }

        if (!ReferenceEquals(seatPassengerField.GetValue(seat), latch.Body.gameObject))
        {
            return false; // never eject a different character from a stale seat reference
        }

        bool oldTeleport = seat.handleExitTeleport;
        seat.handleExitTeleport = false;
        try
        {
            setPassengerMethod.Invoke(seat, new object[] { null });
        }
        catch (Exception exception)
        {
            if (!latch.EjectFailureLogged)
            {
                latch.EjectFailureLogged = true;
                var root = exception.GetBaseException();
                Diag?.Warning(
                    $"{Tag}: local eject failed ({root.GetType().Name}: {root.Message}); " +
                    "vanilla seat lock/exit path remains.");
            }

            return false;
        }
        finally
        {
            seat.handleExitTeleport = oldTeleport;
        }

        return (seatPassengerField.GetValue(seat) as GameObject) == null;
    }

    private void ClearEjectedVelocity(CharacterBody body)
    {
        var motor = body != null ? body.characterMotor : null;
        if (motor is null)
        {
            return;
        }

        motor.velocity = Vector3.zero;
        motor.rootMotion = Vector3.zero;
        var kinematicMotor = motorProperty.GetValue(motor);
        if (kinematicMotor != null && baseVelocityField != null)
        {
            baseVelocityField.SetValue(kinematicMotor, Vector3.zero);
        }
    }

    private void EnableAuthorityMotion(CharacterBody body)
    {
        if (body == null)
        {
            return;
        }

        var motor = body.characterMotor;
        if (motor is not null)
        {
            motor.disableAirControlUntilCollision = false;
        }

        var networkTransform = body.GetComponent<CharacterNetworkTransform>();
        if (networkTransform != null)
        {
            networkTransform.SetTransmitUpdatesAuthority(true);
        }
    }

    private void CheckExpiry(Latch latch)
    {
        if (Time.time <= latch.ExpireAt)
        {
            return;
        }

        Remove(latch, latch.ExitHandled ? "exit handled" : "exit state never arrived");
    }

    private void Remove(Latch latch, string reason)
    {
        if (latch == null)
        {
            return;
        }

        if (latch.Motor is not null)
        {
            latches.Remove(latch.Motor);
        }

        Diag?.Info($"{Tag}: latch cleared ({reason}).");
    }

    private Latch FindLatch(CharacterBody body)
    {
        if (body == null)
        {
            return null;
        }

        var motor = body.characterMotor;
        return motor is not null
               && latches.TryGetValue(motor, out var latch)
               && ReferenceEquals(latch.Body, body)
            ? latch
            : null;
    }

    private bool OwnsGhostCore(CharacterBody body)
    {
        if (!ShouldRun())
        {
            return false;
        }

        var latch = FindLatch(body);
        return latch != null && !latch.VanillaExitSeen;
    }

    private bool SuppressExitSmoothingCore(VehicleShieldDashExit exit)
    {
        if (!ShouldRun())
        {
            return false;
        }

        var passenger = exitPassengerField.GetValue(exit) as CharacterBody;
        var vehicleRoot = ExitVehicle(exit);
        var latch = FindLatch(passenger);
        if (latch != null && VehicleMatches(vehicleRoot, latch))
        {
            return ExitPredatesCurrentDash(latch, vehicleRoot)
                   || (latch.Fired && ShouldAbsorbExit(exit, latch));
        }

        return HistoryMatches(vehicleRoot, passenger, out _);
    }

    private static bool StateSetPrefix(EntityStateMachine __instance, EntityState newState)
    {
        var self = instance;
        if (self == null || newState == null || !(newState is VehicleShieldDashExit exit))
        {
            return true;
        }

        self.Guard(() => self.OnVanillaExitState(__instance, exit));
        return true;
    }

    private void OnVanillaExitState(EntityStateMachine machine, VehicleShieldDashExit exit)
    {
        if (!ShouldRun() || machine == null)
        {
            return;
        }

        var passenger = exitPassengerField.GetValue(exit) as CharacterBody;
        var latch = FindLatch(passenger);
        if (latch != null && VehicleMatches(machine.gameObject, latch))
        {
            if (ExitPredatesCurrentDash(latch, machine.gameObject, out var oldAnimPlayed))
            {
                // The old exit must neither snap in OnEnter nor replay an animation the old
                // local end already played; leave it for OnExit when Feedback did not play one.
                if (oldAnimPlayed)
                {
                    exitAnimationPlayedField.SetValue(exit, true);
                }

                Diag?.Info($"{Tag}: previous dash exit absorbed before this dash's vehicle state.");
                return;
            }

            if (!latch.Fired)
            {
                latch.VanillaExitSeen = true;
                Diag?.Info($"{Tag}: vanilla exit arrived before local end; falling back to vanilla.");
                return;
            }

            // OnEnter runs after this prefix: stop the later OnExit from playing the animation again.
            if (latch.AnimPlayed)
            {
                exitAnimationPlayedField.SetValue(exit, true);
            }

            Diag?.Info(
                $"{Tag}: vanilla exit state seen; absorb={ShouldAbsorbExit(exit, latch)} " +
                $"animPlayed={latch.AnimPlayed} residual={ResidualMeters(exit, latch.Body):F2}m.");
            return;
        }

        // A rapid second dash replaced the latch: the old vehicle's exit is still ours to absorb.
        if (HistoryMatches(machine.gameObject, passenger, out var old))
        {
            if (old.AnimPlayed)
            {
                exitAnimationPlayedField.SetValue(exit, true);
            }

            Diag?.Info($"{Tag}: stale local-end exit state seen; keeping its absorb.");
        }
    }

    /// <summary>
    /// After the local eject a late seat SyncVar may re-enter the passenger; the seat's
    /// InterruptPriority.Vehicle state force would restart the dash proxy, so skip it.
    /// </summary>
    private static bool ForcePassengerStatePrefix(VehicleSeat __instance)
    {
        var self = instance;
        if (self is null || __instance is null)
        {
            return true;
        }

        bool suppress = false;
        self.Guard(() => suppress = self.SuppressForcedState(__instance));
        return !suppress;
    }

    private bool SuppressForcedState(VehicleSeat seat)
    {
        if (!ShouldRun())
        {
            return false;
        }

        var passenger = seatPassengerField.GetValue(seat) as GameObject;
        var body = passenger != null ? passenger.GetComponent<CharacterBody>() : null;
        var latch = FindLatch(body);
        return latch != null && latch.Fired && VehicleMatches(seat.gameObject, latch);
    }

    private static bool ExitAuthorityPrefix(VehicleShieldDashExit __instance)
    {
        var self = instance;
        if (self is null || __instance is null)
        {
            return true;
        }

        bool suppress = false;
        self.Guard(() => suppress = self.SuppressExitAuthority(__instance));
        return !suppress;
    }

    private bool SuppressExitAuthority(VehicleShieldDashExit exit)
    {
        if (!ShouldRun())
        {
            return false;
        }

        var passenger = exitPassengerField.GetValue(exit) as CharacterBody;
        var vehicleRoot = ExitVehicle(exit);
        var latch = FindLatch(passenger);
        if (latch != null && VehicleMatches(vehicleRoot, latch))
        {
            if (ExitPredatesCurrentDash(latch, vehicleRoot, out var oldAnimPlayed))
            {
                ApplyExitAbsorb(exit, latch.Body, oldAnimPlayed, restoreMotion: false);
                return true;
            }

            if (!latch.Fired || !ShouldAbsorbExit(exit, latch))
            {
                return false; // teleport/respawn: after the early animation, let vanilla snap.
            }

            ApplyExitAbsorb(exit, latch.Body, latch.AnimPlayed, restoreMotion: true);
            latch.ExitHandled = true;
            return true;
        }

        // Rapid second dash: absorb the previous dash's late exit for the old vehicle too.
        // Do not touch motion/transmit: the new dash's own seat state owns them now.
        if (HistoryMatches(ExitVehicle(exit), passenger, out var old))
        {
            ApplyExitAbsorb(exit, passenger, old.AnimPlayed, restoreMotion: false);
            return true;
        }

        return false;
    }

    private void ApplyExitAbsorb(
        VehicleShieldDashExit exit, CharacterBody body, bool animPlayed, bool restoreMotion)
    {
        var seat = exit.outer != null ? exit.outer.gameObject.GetComponent<VehicleSeat>() : null;
        if (seat != null)
        {
            Unsubscribe(seat, exit);
        }

        if (animPlayed)
        {
            exitAnimationPlayedField.SetValue(exit, true);
        }

        if (restoreMotion)
        {
            EnableAuthorityMotion(body);
        }

        Diag?.Info(
            $"{Tag}: vanilla exit absorbed: no snap, no replay " +
            $"(residual={ResidualMeters(exit, body):F2}m anim={animPlayed} stale={!restoreMotion}).");
    }

    private void Unsubscribe(VehicleSeat seat, VehicleShieldDashExit exit)
    {
        if (exitAuthorityMethod == null)
        {
            return;
        }

        try
        {
            var handler = (Action<GameObject>)Delegate.CreateDelegate(
                typeof(Action<GameObject>), exit, exitAuthorityMethod);
            seat.onPassengerExit -= handler;
        }
        catch (Exception)
        {
            // A mismatched delegate only leaves an inert subscription on an exiting state.
        }
    }

    private bool ShouldAbsorbExit(VehicleShieldDashExit exit, Latch latch)
    {
        if (latch == null || latch.Body == null || exitPassengerField == null)
        {
            return false;
        }

        bool matches = ReferenceEquals(exitPassengerField.GetValue(exit), latch.Body);
        return PredictionMath.ShouldAbsorbVanillaExit(
            latch.Fired, matches, ResidualMeters(exit, latch.Body), latch.PredictedTravel);
    }

    private double ResidualMeters(VehicleShieldDashExit exit, CharacterBody body)
    {
        if (body == null)
        {
            return -1.0;
        }

        var delta = body.transform.position - exit.vehicleAuthorityPosition;
        delta.y = 0f;
        return delta.magnitude;
    }

    private bool VehicleMatches(GameObject vehicleRoot, Latch latch)
    {
        if (vehicleRoot == null || latch == null)
        {
            return false;
        }

        if (latch.Vehicle != null)
        {
            return latch.Vehicle == vehicleRoot;
        }

        // The SyncVar id survives the vehicle object being destroyed; a later pod/seat
        // must not be mistaken for the dash vehicle.
        if (latch.VehicleNetId != 0)
        {
            var identity = vehicleRoot.GetComponent<NetworkIdentity>();
            return identity != null && identity.netId.Value == latch.VehicleNetId;
        }

        var found = FindVehicle(latch.Body);
        if (found != null)
        {
            latch.Vehicle = found;
            RefreshLatchVehicleId(latch);
            return found == vehicleRoot;
        }

        return true; // vehicle identity not known yet: do not block our own dash.
    }

    private static void RefreshLatchVehicleId(Latch latch)
    {
        if (latch == null || latch.VehicleNetId != 0 || latch.Vehicle == null)
        {
            return;
        }

        var identity = latch.Vehicle.GetComponent<NetworkIdentity>();
        if (identity != null)
        {
            latch.VehicleNetId = identity.netId.Value;
        }
    }

    private void RecordHistory(CharacterBody body, Latch latch)
    {
        if (body == null)
        {
            return;
        }

        history.Remove(body);
        history.Add(body, new LocalEndHistory
        {
            VehicleNetId = latch.VehicleNetId,
            FiredAt = Time.time,
            WindowSeconds = PredictionMath.LocalEndLatchSeconds(2.0 * latch.OneWay),
            AnimPlayed = latch.AnimPlayed,
        });
    }

    private bool HistoryMatches(GameObject vehicleRoot, CharacterBody body, out LocalEndHistory entry)
    {
        entry = null;
        if (vehicleRoot == null || body == null || !history.TryGetValue(body, out var found))
        {
            return false;
        }

        if (found.VehicleNetId == 0 || Time.time > found.FiredAt + found.WindowSeconds)
        {
            return false;
        }

        var identity = vehicleRoot.GetComponent<NetworkIdentity>();
        if (identity == null || identity.netId.Value != found.VehicleNetId)
        {
            return false;
        }

        entry = found;
        return true;
    }

    private static uint ReadVehicleNetId(CharacterBody body)
    {
        if (body == null || vehicleIdField == null)
        {
            return 0;
        }

        var controller = body.GetComponent<EnforcerVehicleDashController>();
        if (controller == null)
        {
            return 0;
        }

        return vehicleIdField.GetValue(controller) is NetworkInstanceId netId ? netId.Value : 0;
    }

    private static GameObject ExitVehicle(VehicleShieldDashExit exit)
        => exit != null && exit.outer != null ? exit.outer.gameObject : null;

    private static GameObject FindVehicle(CharacterBody body)
    {
        if (body == null || controllerVehicleProperty == null)
        {
            return null;
        }

        var controller = body.GetComponent<EnforcerVehicleDashController>();
        return controller != null
            ? controllerVehicleProperty.GetValue(controller) as GameObject
            : null;
    }

    private static bool Alive(CharacterBody body)
        => body != null && body.healthComponent != null && body.healthComponent.alive;
}
