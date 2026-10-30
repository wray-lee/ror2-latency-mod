// 票P7 EnforcerGhostDash：松键后本地立即模拟冲刺位移，载具到达时平滑交接（实验项，默认开）。
//
// 反编译核对（F:/ror2-decomp/RoR2src，只读）：
// - EntityStates.Enforcer/ProxyShieldDash.cs:128 OnEnter：:141 `if (base.isAuthority)`；
//   :143 positionFromAuthority = transform.position；:146 forwardRotation = characterDirection.forward
//   （无 characterDirection 时 modelTransform/transform.forward）；:156-158
//   aimDirection 与 forwardRotation 的夹角截断到 maximumYAngle，带 aim.y 符号，绕 -Vector3.right 旋转
//   → aimRotationOffsetFromAuthority；:164 ActivateVehicle(positionFromAuthority)；
//   :166-172 仅 NetworkServer.active 才 InitializeStateServer/SetVehicleStateServer/EnterVehicleServer。
// - InitializeStateServer(:186-216)：:210 num = inOverrideSpeedCoefficientFromAuthority *
//   inOverrideDurationFromAuthority；:211 b = moveSpeedStat / Mathf.Max(baseMoveSpeed *
//   sprintingSpeedMultiplier, 1f)；:212 num *= Mathf.Lerp(1f, b, normalizedBoostFromSpeedItems)；
//   :213-214 inDurationAuthority = 覆盖时长、inSpeedCoefficientFromAuthority = num / 时长
//   （乘除抵消后 = 覆盖速度系数 × Lerp 项）；:215 下发 aim 偏移。
// - 客户端 proxy 自带 inOverrideDurationFromAuthority(:106 默认 1f)、
//   inOverrideSpeedCoefficientFromAuthority(:108 默认 50f)，由
//   BaseShieldDashPrepCharge.cs:267-277 ModifyNextState 从 prepPassingParamaters 赋值——本地客户端
//   在 OnEnter 前即可读，且 OnSerialize(:110-117) 送去的就是这两个值。
// - 速度方向：VehicleShieldDashHeavy.cs:102-119 forwardDirection = 乘客 characterDirection.forward；
//   :304-309 GetIdealVelocityAtT(t) = forwardVelocityCurve.Evaluate(t) *
//   inSpeedCoefficientFromAuthority * (QuaternionSafeLookRotation(forwardDirection) *
//   inAimRotationOffsetFromAuthority * Vector3.forward)。forwardVelocityCurve 是载具状态配置
//   (:26-27)，客户端 proxy 没有；本补丁用 `new VehicleShieldDashHeavy/Light()` 本地实例读
//   EntityStateCatalog 载入的同一份曲线配置（EntityState.cs:87-90 构造时 InitializeStateFields）。
//   读不到时按 1.0 常数近似并写日志。
// - 交接触发：ProxyShieldDash.FixedUpdate :254-259 第一次观察到 vehicleState is T 时置
//   vehicleEnteredState；退场硬贴走 VehicleShieldDashExit.OnPassengerExitAuthority(:124-144)，
//   已由 EnforcerExitSmoothing 覆盖。
// - 座位到达时刻的「position」不可信（2026-10-23 日志核对）：客户端车辆 Main 状态
//   （VehicleShieldDashMain.cs:20-30）对本机 SetReceiveUpdatesRemote(false)，客户端在
//   ProxyShieldDash.OnEnter :164 只把车摆到上报起点；VehicleShieldDashHeavy.OnEnter :142-143
//   收到 msg48 才 ClearSnapshots + 开始接收服务器位置，第一帧快照才把车拉到服务器当前位置。
//   因此 seat 到达帧的 seatPosition 仍在上报起点，car 真正位置晚 1-2 帧才到；用该帧的
//   gap 判定交接必然误判（旧版 gap≈幽灵位移 12.4m）。
// - 轨迹一致：StartLead 在幽灵拥有冲刺时上报 S0 + 冲刺初速×U（时间封顶 0.25s、遇墙缩短），
//   本类分两段模拟同一预测：lead 段（leadDistance/leadSeconds 匀速）+ 完整冲刺曲线
//   （Duration 秒）。幽灵终点 = 上报起点 + 冲刺位移 = 服务器载具终点，客户端不再故意少走
//   一段再在交接时快进；0.25s 只封顶 lead，不封顶冲刺本身（否则 RTT>0.25s 时必然追不上）。
// - 座位硬贴：VehicleSeat.cs:511-538 UpdatePassengerPosition（:527 Motor.SetPosition），
//   FixedUpdate :497-500 / LateUpdate :503-508 每帧调用。本补丁只对「本机、本幽灵正在 hold
//   的那名乘客」在 prefix 返回 false：幽灵到位后 hold 在原地，等 seat 沿冲刺方向的投影追上
//   （PredictionMath.HandoffCaughtUp，含 2m 插值滞后容差/2.5m 侧向容差），再用 0.1–0.2s
//   插值并入 seat 实时位置；hold 超时 max(2×RTT,0.5s) 时若在相对传送阈值内也直接并入。
//   座位被弹出（载具退场）→ 释放，交给 EnforcerExitSmoothing 处理退场硬贴；任何异常/关闭/
//   熔断都立即释放并还原原版路径。交接由该 prefix 驱动，因为该座位通常
//   disablePassengerMotor=true（VehicleSeat.cs:708-713 会禁用乘客 CharacterMotor），
//   CharacterMotor.BeforeCharacterUpdate 在入座后不再运行。前缀返回 false 时原版的
//   SetRotation/velocity 清零也被跳过，只影响 hold/blend 窗口（motor 已禁用，影响可忽略）。
// - 终止条件：入座前——技能状态退出/死亡/失去权威/外部传送式位移/超时；入座后——死亡、失去
//   权威、座位丢失或换座、外部传送式位移、相对传送阈值、配置关闭/熔断。入座后 Proxy 状态
//   退出（载具状态离开 T）不终止交接：位置由 seat 管，Proxy 只是动画/相机层。
//   伤害/命中仍由主机载具 OverlapAttack 判定。
// - 挂点全部非泛型：EntityStateMachine.SetState(:174-199)、CharacterMotor.BeforeCharacterUpdate、
//   VehicleSeat.UpdatePassengerPosition。绝不 Patch 泛型基类 ProxyShieldDash<T> 的
//   OnEnter/FixedUpdate：Heavy/Light 解析出的 MethodInfo 元数据相同但属不同构造泛型，
//   Harmony PatchInfo.Add 不去重会叠加执行（EnforcerFeedbackPatch.cs 头注释已论证）。
// - StartLead 交互：本类暴露 TryGetGhostLeadInputs（起点/冲刺方向/初速）与 SetGhostLead
//   （记录 StartLead 实际写入的 lead）。lead 只在 EnforcerStartLeadPatch 里计算一次；
//   幽灵按同一值模拟，避免「幽灵本地前移 + 起点前推」双重补偿。幽灵的位移会经玩家既有
//   CharacterNetworkTransform 同步给服务器；若 StartLead 关闭/熔断/未发送，服务器载具仍从
//   S0 开始，座位会把服务器玩家拉回（其它客户端看到旧的回拉）。因此没有 LeadRecorded 时
//   幽灵在第一步就停止，退回原版行为——GhostDash 依赖 StartLead 开启。
// - 仅 Remote Client 生效；只改本地位置呈现，不判定伤害/命中，不新增网络消息。

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

public sealed class EnforcerGhostDashPatch : SkillPatch
{
    private const string Tag = "EnforcerGhostDash";

    /// <summary>Below this a handoff is already seamless; keep vanilla.</summary>
    private const float MinHandoffMeters = 0.05f;

    /// <summary>Slack for one simulation step before a position change counts as external.</summary>
    private const float TeleportSlackMeters = 2.5f;

    private static EnforcerGhostDashPatch instance;
    private static PropertyInfo motorProperty;
    private static PropertyInfo transientPositionProperty;
    private static MethodInfo setPositionMethod;
    private static FieldInfo seatPassengerField;
    private static PropertyInfo controllerVehicleProperty;

    private static readonly Dictionary<Type, Type> ProxyToVehicleState = new Dictionary<Type, Type>
    {
        { typeof(ProxyShieldDashHeavy), typeof(VehicleShieldDashHeavy) },
        { typeof(ProxyShieldDashLight), typeof(VehicleShieldDashLight) },
    };

    private readonly Dictionary<Type, FieldInfo> vehicleEnteredByType = new Dictionary<Type, FieldInfo>();
    private readonly Dictionary<Type, FieldInfo> forwardRotationByType = new Dictionary<Type, FieldInfo>();
    private readonly Dictionary<Type, FieldInfo> aimOffsetByType = new Dictionary<Type, FieldInfo>();
    private readonly Dictionary<Type, FieldInfo> speedBoostByType = new Dictionary<Type, FieldInfo>();
    private readonly Dictionary<Type, AnimationCurve> curvesByVehicleType = new Dictionary<Type, AnimationCurve>();
    private readonly Dictionary<Type, string> curveSourceByVehicleType = new Dictionary<Type, string>();

    // Weak keys: a body destroyed mid-ghost must not stay referenced.
    private readonly ConditionalWeakTable<CharacterMotor, GhostState> states =
        new ConditionalWeakTable<CharacterMotor, GhostState>();

    // Cheap per-character gate: BeforeCharacterUpdate/VehicleSeat run every frame for everyone.
    private int activeStates;

    // P10: exit smoothing needs the dash's predicted travel, but the ghost is usually stopped
    // before the vanilla Exit state arrives. Last value wins; older than 2 s means no prediction.
    private const double PredictedTravelFreshSeconds = 2.0;
    private static double lastPredictedTravelMeters;
    private static double lastPredictedTravelTime = double.NegativeInfinity;
    private static bool lastPredictedTravelRecorded;

    /// <summary>P10: last dash's predicted travel for ExitSmoothing; 0 when stale/never armed.</summary>
    internal static double RecentPredictedTravelMeters()
    {
        if (!lastPredictedTravelRecorded)
        {
            return 0.0;
        }

        double age = Time.time - lastPredictedTravelTime;
        return age >= 0.0 && age <= PredictedTravelFreshSeconds ? lastPredictedTravelMeters : 0.0;
    }

    private static void RememberPredictedTravel(GhostState ghost)
    {
        lastPredictedTravelMeters = ghost.Initialized ? ghost.PredictedTravel : 0.0;
        lastPredictedTravelTime = Time.time;
        lastPredictedTravelRecorded = true;
    }

    /// <summary>Read-only ghost view for EnforcerLocalEnd (arming, firing, watchdog).</summary>
    internal struct LocalEndGhostStatus
    {
        public bool Found;
        public CharacterBody Body;
        public EntityStateMachine BodyMachine;
        public EntityState BodyState;
        public double StartTime;
        public double MotionSeconds;
        public double Duration;
        public double TraveledSeconds;
        public double PredictedTravel;
        public double OneWay;
        public bool LeadRecorded;
        public bool Handoff;
        public bool MotionDone;
        public bool StateExited;
        public bool VehicleStateSeen;
    }

    private sealed class GhostState
    {
        public CharacterBody Body;
        public CharacterMotor CharacterMotor;
        public object Motor;
        public EntityStateMachine Machine;
        public EntityState BodyState;
        public FieldInfo VehicleEnteredField;
        public FieldInfo ForwardRotationField;
        public FieldInfo AimOffsetField;
        public bool Initialized;
        public Vector3 Origin;
        public Vector3 Position;
        public Vector3 LastApplied;
        public Vector3 Direction;
        public Quaternion AimOffset;
        public Vector3 CoreOffset;
        public float DirectionScale;
        public float SpeedCoefficient;
        public float Duration;
        public double InitialSpeed;
        public double DashDistance;
        public double LeadSeconds;
        public double LeadDistance;
        public bool LeadRecorded;
        public AnimationCurve Curve;
        public string CurveSource;
        public double StartTime;
        public double TraveledSeconds;
        public double OneWay;
        public double TimeoutSecondsCached;
        public double TimeoutRttCached;
        public bool WallStopped;
        public bool VehicleStateSeen;
        public bool Handoff;
        public bool Blending;
        public VehicleSeat Seat;
        public Vector3 HoldFrom;
        public double HoldStartTime;
        public double HoldTimeoutSeconds;
        public Vector3 BlendFrom;
        public double BlendStartTime;
        public float BlendDuration;
        public bool LoggedFirstStep;
        public bool MotionDoneLogged;

        public double MotionSeconds => LeadSeconds + Duration;

        public double PredictedTravel => LeadDistance + DashDistance;
    }

    private readonly Func<float> fixedTimeoutSeconds;
    private readonly Func<float> timeoutMaxSeconds;

    public EnforcerGhostDashPatch(
        Func<bool> isEnabled,
        Func<float> fixedTimeoutSeconds,
        Func<float> timeoutMaxSeconds,
        DiagLog diag = null,
        LatencyEstimator latency = null)
        : base("EnforcerGhostDash", PatchSide.Client, isEnabled, diag, latency)
    {
        this.fixedTimeoutSeconds = fixedTimeoutSeconds;
        this.timeoutMaxSeconds = timeoutMaxSeconds;
    }

    protected override void Install()
    {
        motorProperty = typeof(CharacterMotor).GetProperty("Motor");
        seatPassengerField = AccessTools.Field(typeof(VehicleSeat), "passengerBodyObject");
        controllerVehicleProperty = typeof(EnforcerVehicleDashController).GetProperty(
            "currentVehicle", BindingFlags.Instance | BindingFlags.NonPublic);

        var setState = AccessTools.Method(
            typeof(EntityStateMachine), "SetState", new[] { typeof(EntityState) });
        var beforeCharacterUpdate = AccessTools.Method(
            typeof(CharacterMotor), "BeforeCharacterUpdate", new[] { typeof(float) });
        var seatUpdate = AccessTools.Method(
            typeof(VehicleSeat), "UpdatePassengerPosition", new[] { typeof(Vector3), typeof(Quaternion) });
        if (motorProperty == null || seatPassengerField == null || controllerVehicleProperty == null
            || setState == null || beforeCharacterUpdate == null || seatUpdate == null)
        {
            throw new MissingMemberException(
                "EnforcerGhostDash members missing: " +
                $"Motor={motorProperty != null}, passengerBodyObject={seatPassengerField != null}, " +
                $"controller.currentVehicle={controllerVehicleProperty != null}, " +
                $"EntityStateMachine.SetState={setState != null}, " +
                $"BeforeCharacterUpdate={beforeCharacterUpdate != null}, " +
                $"VehicleSeat.UpdatePassengerPosition={seatUpdate != null}");
        }

        // KinematicCharacterController is not referenced by the csproj; resolve its members at runtime.
        var motorType = motorProperty.PropertyType;
        transientPositionProperty = motorType.GetProperty("TransientPosition");
        setPositionMethod = motorType.GetMethod("SetPosition", new[] { typeof(Vector3), typeof(bool) });
        if (transientPositionProperty == null || setPositionMethod == null)
        {
            throw new MissingMemberException(
                $"{motorType.FullName} members missing: TransientPosition={transientPositionProperty != null}, " +
                $"SetPosition={setPositionMethod != null}");
        }

        foreach (var pair in ProxyToVehicleState)
        {
            var entered = FindField(pair.Key, "vehicleEnteredState");
            var forwardRotation = FindField(pair.Key, "forwardRotation");
            var aimOffset = FindField(pair.Key, "aimRotationOffsetFromAuthority");
            var speedBoost = FindField(pair.Key, "normalizedBoostFromSpeedItems");
            if (entered == null || forwardRotation == null || aimOffset == null || speedBoost == null)
            {
                throw new MissingMemberException(
                    $"{pair.Key.FullName} members missing: vehicleEnteredState={entered != null}, " +
                    $"forwardRotation={forwardRotation != null}, " +
                    $"aimRotationOffsetFromAuthority={aimOffset != null}, " +
                    $"normalizedBoostFromSpeedItems={speedBoost != null}");
            }

            vehicleEnteredByType[pair.Key] = entered;
            forwardRotationByType[pair.Key] = forwardRotation;
            aimOffsetByType[pair.Key] = aimOffset;
            speedBoostByType[pair.Key] = speedBoost;
        }

        // csproj 未引用 PhysicsModule；复用 StartLead 的运行期反射（同一探针每步调用）。
        EnforcerStartLeadPatch.ResolveRaycast();

        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            setState,
            prefix: new HarmonyMethod(typeof(EnforcerGhostDashPatch), nameof(StateSetPrefix)));
        harmony.Patch(
            beforeCharacterUpdate,
            postfix: new HarmonyMethod(typeof(EnforcerGhostDashPatch), nameof(BeforeCharacterUpdatePostfix)));
        harmony.Patch(
            seatUpdate,
            prefix: new HarmonyMethod(typeof(EnforcerGhostDashPatch), nameof(UpdatePassengerPositionPrefix)));

        instance = this;
        Diag?.Info(
            $"{Tag}: installed (local dash ghost on ProxyShieldDash entry, lead + full dash curve, " +
            "seat-snap suppression for the local ghost only, hold-until-caught-up then 0.1-0.2s blend).");
    }

    /// <summary>
    /// StartLead queries this once before sending msg48. True only while this ghost belongs to the
    /// state being serialized, so a stale ghost can never move another dash's start point.
    /// The ghost owns the lead computation too: its reported start and its simulated path must
    /// be the same point, or the client has to fast-forward at handoff (double compensation).
    /// </summary>
    internal static bool TryGetGhostLeadInputs(
        EntityState state, out Vector3 origin, out Vector3 direction, out double speed)
    {
        origin = default;
        direction = default;
        speed = 0.0;
        var self = instance;
        if (self == null || state?.outer == null)
        {
            return false;
        }

        if (!self.ShouldRun())
        {
            return false;
        }

        var motor = state.outer.commonComponents.characterMotor;
        if (motor is null || !self.states.TryGetValue(motor, out var ghost))
        {
            return false;
        }

        if (ghost.Machine != state.outer || ghost.BodyState != state)
        {
            return false;
        }

        if (!ghost.Initialized)
        {
            var initialized = false;
            self.Guard(() => initialized = self.InitializeGhost(ghost));
            if (!initialized)
            {
                return false;
            }
        }

        origin = ghost.Origin;
        direction = ghost.Direction;
        speed = Math.Max(0.0, ghost.InitialSpeed);
        return true;
    }

    /// <summary>
    /// Records the lead StartLead actually wrote, so the ghost simulates the same forecast:
    /// lead distance first, then the vanilla dash curve. Called only from the msg48 send hook.
    /// </summary>
    internal static void SetGhostLead(EntityState state, double leadSeconds, double leadDistance)
    {
        var self = instance;
        self?.Guard(() => self.ApplyGhostLead(state, leadSeconds, leadDistance));
    }

    /// <summary>EnforcerLocalEnd: true when this body state is one of the dash proxies we simulate.</summary>
    internal static bool IsDashProxyState(EntityState state)
        => state != null && ProxyToVehicleState.ContainsKey(state.GetType());

    /// <summary>
    /// EnforcerLocalEnd scheduling seam: read the ghost after the normal step. False when the
    /// patch is off/tripped or this motor has no ghost, so local end never invents a prediction.
    /// </summary>
    internal static bool TryGetLocalEndStatus(CharacterMotor motor, out LocalEndGhostStatus status)
    {
        status = default;
        var self = instance;
        if (self is null || motor is null || !self.ShouldRun())
        {
            return false;
        }

        if (!self.states.TryGetValue(motor, out var ghost))
        {
            return false;
        }

        status = Snapshot(ghost);
        return true;
    }

    /// <summary>
    /// EnforcerLocalEnd step seam for the seated window: the passenger motor is usually
    /// disabled, so VehicleSeat.UpdatePassengerPosition is the only tick left.
    /// </summary>
    internal static bool StepLocalEndGhost(
        CharacterMotor motor, float deltaTime, out LocalEndGhostStatus status)
    {
        status = default;
        var self = instance;
        if (self is null || motor is null || !self.ShouldRun())
        {
            return false;
        }

        if (!self.states.TryGetValue(motor, out var ghost))
        {
            return false;
        }

        self.Guard(() => self.StepGhost(ghost, deltaTime));
        if (!self.states.TryGetValue(motor, out ghost))
        {
            return false;
        }

        status = Snapshot(ghost);
        return true;
    }

    internal static void StopLocalEndGhost(CharacterMotor motor, string reason)
    {
        var self = instance;
        if (self is null || motor is null)
        {
            return;
        }

        if (self.states.TryGetValue(motor, out var ghost))
        {
            self.Guard(() => self.Stop(ghost, reason));
        }
    }

    private static LocalEndGhostStatus Snapshot(GhostState ghost)
        => new LocalEndGhostStatus
        {
            Found = true,
            Body = ghost.Body,
            BodyMachine = ghost.Machine,
            BodyState = ghost.BodyState,
            StartTime = ghost.StartTime,
            MotionSeconds = ghost.MotionSeconds,
            Duration = ghost.Duration,
            TraveledSeconds = ghost.TraveledSeconds,
            PredictedTravel = ghost.PredictedTravel,
            OneWay = ghost.OneWay,
            LeadRecorded = ghost.LeadRecorded,
            Handoff = ghost.Handoff,
            MotionDone = ghost.WallStopped || ghost.TraveledSeconds >= ghost.MotionSeconds - 1e-6,
            StateExited = ghost.Machine == null || ghost.Machine.state != ghost.BodyState,
            VehicleStateSeen = ghost.VehicleStateSeen,
        };

    private void ApplyGhostLead(EntityState state, double leadSeconds, double leadDistance)
    {
        var motor = state?.outer?.commonComponents.characterMotor;
        if (motor is null || !states.TryGetValue(motor, out var ghost) || ghost.BodyState != state)
        {
            return;
        }

        // msg48 is sent once per state; a mod-triggered re-send must not rebuild the path
        // under an already moving ghost (the reported start would no longer match).
        if (ghost.LeadRecorded || ghost.TraveledSeconds > 0.0 || ghost.Handoff)
        {
            Diag?.Warning($"{Tag}: duplicate lead ignored (already moving/handed off).");
            return;
        }

        ghost.LeadRecorded = true;
        ghost.LeadDistance = Math.Max(0.0, leadDistance);
        ghost.LeadSeconds = ghost.LeadDistance > 0.001 ? Math.Max(0.0, leadSeconds) : 0.0;
        Diag?.Info(
            $"{Tag}: lead recorded for ghost: {ghost.LeadSeconds:F3}s / {ghost.LeadDistance:F2}m " +
            $"(dash curve adds {ghost.DashDistance:F2}m, total predicted {ghost.PredictedTravel:F2}m).");
    }

    private static void StateSetPrefix(EntityStateMachine __instance, EntityState newState)
    {
        var self = instance;
        if (self == null || newState == null || !ProxyToVehicleState.ContainsKey(newState.GetType()))
        {
            return;
        }

        self.Guard(() => self.BeginGhost(__instance, newState));
    }

    private static void BeforeCharacterUpdatePostfix(CharacterMotor __instance, float deltaTime)
    {
        var self = instance;
        if (self == null || self.activeStates == 0 || !self.states.TryGetValue(__instance, out var ghost))
        {
            return;
        }

        self.Guard(() => self.StepGhost(ghost, deltaTime));
    }

    private static bool UpdatePassengerPositionPrefix(VehicleSeat __instance)
    {
        var self = instance;
        if (self == null)
        {
            return true;
        }

        // Local early end runs even after the ghost is gone (late seat re-entry), so this
        // must stay ahead of the activeStates gate.
        if (EnforcerLocalEndPatch.TryControlSeat(__instance))
        {
            return false;
        }

        if (self.activeStates == 0)
        {
            return true;
        }

        bool skip = false;
        self.Guard(() => skip = self.ShouldSkipSeatSnap(__instance));
        return !skip;
    }

    private void BeginGhost(EntityStateMachine machine, EntityState state)
    {
        if (!ShouldRun())
        {
            return;
        }

        var components = machine.commonComponents;
        var body = components.characterBody;
        var motor = components.characterMotor;
        if (body == null || motor is null || body.healthComponent == null || !body.healthComponent.alive)
        {
            return;
        }

        if (!Util.HasEffectiveAuthority(machine.networkIdentity) || body.currentVehicle != null)
        {
            return;
        }

        if (Latency == null || !Latency.TryGetOneWayLatency(out var oneWay) || oneWay <= 0.0)
        {
            Diag?.Throttled(
                $"{Tag}.nolatency",
                $"{Tag}: no ghost, one-way latency unavailable (host or no RTT sample).");
            return;
        }

        object reflectedMotor = motorProperty.GetValue(motor);
        if (reflectedMotor == null)
        {
            return;
        }

        // Speed/direction are read on the first step, after SetState has run ModifyNextState
        // (BaseShieldDashPrepCharge.cs:267-277) and ProxyShieldDash.OnEnter (:143-158): at this
        // prefix those fields still hold their defaults. StartLead only needs Position, available now.
        var position = (Vector3)transientPositionProperty.GetValue(reflectedMotor);
        var ghost = new GhostState
        {
            Body = body,
            CharacterMotor = motor,
            Motor = reflectedMotor,
            Machine = machine,
            BodyState = state,
            VehicleEnteredField = vehicleEnteredByType[state.GetType()],
            ForwardRotationField = forwardRotationByType[state.GetType()],
            AimOffsetField = aimOffsetByType[state.GetType()],
            Origin = position,
            Position = position,
            LastApplied = position,
            CoreOffset = body.corePosition - body.transform.position,
            StartTime = Time.time,
            OneWay = oneWay,
        };
        // Capture once: a late sample from a previous dash must not shrink this dash's
        // timeout below the elapsed time and force an immediate stop (same as Feedback).
        ghost.TimeoutRttCached = TimeoutRttSeconds();
        ghost.TimeoutSecondsCached = TimeoutSeconds(ghost.TimeoutRttCached);

        bool rearming = states.TryGetValue(motor, out _);
        states.Remove(motor);
        states.Add(motor, ghost);
        if (!rearming)
        {
            activeStates++;
        }

        Diag?.Info(
            $"{Tag}: ghost armed state={state.GetType().Name} " +
            $"pos=({position.x:F2},{position.y:F2},{position.z:F2}) U={oneWay:F3}s RTT={2.0 * oneWay:F3}s " +
            $"cap={PredictionMath.StartLeadCapSeconds:F2}s " +
            $"timeout={ghost.TimeoutSecondsCached:F2}s timeoutRTT={ghost.TimeoutRttCached:F3}s {Latency?.Describe()} " +
            "speed/direction resolved on first step (after ModifyNextState/OnEnter).");
    }

    /// <summary>
    /// Reads the values that OnEnter/ModifyNextState fill in: duration/speed override (interface),
    /// forwardRotation + aimRotationOffsetFromAuthority (private proxy fields, exact aim direction),
    /// forwardVelocityCurve (vehicle-state config) and the local stat scale from InitializeStateServer.
    /// </summary>
    private bool InitializeGhost(GhostState ghost)
    {
        if (!(ghost.BodyState is IShieldDashSpeedAndDuration speedAndDuration))
        {
            Stop(ghost, "state does not expose IShieldDashSpeedAndDuration");
            return false;
        }

        float duration = speedAndDuration.inOverrideDurationFromAuthority;
        float overrideCoefficient = speedAndDuration.inOverrideSpeedCoefficientFromAuthority;
        if (duration <= 0f || overrideCoefficient <= 0f)
        {
            Stop(ghost, $"invalid duration={duration:F2}s coefficient={overrideCoefficient:F2}");
            return false;
        }

        var forwardRotation = (Vector3)ghost.ForwardRotationField.GetValue(ghost.BodyState);
        var aimOffset = (Quaternion)ghost.AimOffsetField.GetValue(ghost.BodyState);
        if (forwardRotation.sqrMagnitude < 1e-6f)
        {
            Stop(ghost, "forwardRotation is zero");
            return false;
        }

        // VehicleShieldDashHeavy.cs:304-309 direction, projected to the horizontal plane
        // (the player motor is pinned horizontally; vertical aim is logged as an assumption).
        Vector3 direction3D = Quaternion.LookRotation(forwardRotation, Vector3.up)
                              * aimOffset
                              * Vector3.forward;
        Vector3 horizontal = new Vector3(direction3D.x, 0f, direction3D.z);
        float directionScale = horizontal.magnitude;
        if (directionScale < 1e-4f)
        {
            Stop(ghost, "aim direction is vertical");
            return false;
        }

        horizontal /= directionScale;

        // InitializeStateServer :210-214, evaluated with the local stats the client already has.
        // shortcut: horizontal-only motion with local stats/curve; if real-room logs show a large
        // handoff gap, drive the client vehicle or replay the server curve timeline instead.
        float boost = (float)speedBoostByType[ghost.BodyState.GetType()].GetValue(ghost.BodyState);
        float b = ghost.Body.moveSpeed
                  / Mathf.Max(ghost.Body.baseMoveSpeed * ghost.Body.sprintingSpeedMultiplier, 1f);
        float speedCoefficient = overrideCoefficient * Mathf.Lerp(1f, b, boost);
        var vehicleType = ProxyToVehicleState[ghost.BodyState.GetType()];
        var curve = GetCurve(vehicleType, out var curveSource);

        ghost.Direction = horizontal;
        ghost.AimOffset = aimOffset;
        ghost.DirectionScale = directionScale;
        ghost.SpeedCoefficient = speedCoefficient;
        ghost.Duration = duration;
        ghost.Curve = curve;
        ghost.CurveSource = curveSource;
        ghost.InitialSpeed = (curve != null ? curve.Evaluate(0f) : 1f) * speedCoefficient * directionScale;
        ghost.DashDistance = MeanCurveValue(curve) * duration * speedCoefficient * directionScale;
        ghost.Initialized = true;

        float pitch = Mathf.Asin(Mathf.Clamp(direction3D.y, -1f, 1f)) * Mathf.Rad2Deg;
        Diag?.Info(
            $"{Tag}: ghost init state={ghost.BodyState.GetType().Name} " +
            $"dir=({horizontal.x:F2},{horizontal.z:F2}) horizontalScale={directionScale:F3} " +
            $"pitch={pitch:F1}deg speedCoeff={speedCoefficient:F2}m/s initialSpeed={ghost.InitialSpeed:F2}m/s " +
            $"overrideCoeff={overrideCoefficient:F2} duration={duration:F2}s dashDistance={ghost.DashDistance:F2}m " +
            $"curve={curveSource} assumptions: horizontal-only motion, local moveSpeed stat, " +
            "curve evaluated at t=elapsed/duration.");
        return true;
    }

    /// <summary>Mean value of the dash velocity curve over its duration (midpoint rule, 16 samples).</summary>
    private static double MeanCurveValue(AnimationCurve curve)
    {
        if (curve == null)
        {
            return 1.0;
        }

        const int samples = 16;
        double sum = 0.0;
        for (var i = 0; i < samples; i++)
        {
            sum += curve.Evaluate((i + 0.5f) / samples);
        }

        return sum / samples;
    }

    /// <summary>
    /// The host vehicle re-reads the passenger's characterDirection every step
    /// (VehicleShieldDashHeavy.cs:102-118, fed by ProxyShieldDash.cs:221-233), so steering bends
    /// the real dash; follow the same local direction or a long dash ends far off the host's.
    /// </summary>
    private static void FollowSteering(GhostState ghost)
    {
        var characterDirection = ghost.Body.characterDirection;
        if (!characterDirection || characterDirection.forward.sqrMagnitude < 1e-6f)
        {
            return;
        }

        Vector3 direction3D = Quaternion.LookRotation(characterDirection.forward, Vector3.up)
                              * ghost.AimOffset
                              * Vector3.forward;
        Vector3 horizontal = new Vector3(direction3D.x, 0f, direction3D.z);
        float scale = horizontal.magnitude;
        if (scale < 1e-4f)
        {
            return;
        }

        ghost.Direction = horizontal / scale;
        ghost.DirectionScale = scale;
    }

    private void StepGhost(GhostState ghost, float deltaTime)
    {
        var body = ghost.Body;
        if (!ShouldRun())
        {
            Stop(ghost, "config off / host / tripped");
            return;
        }

        if (body == null || !ReferenceEquals(body.characterMotor, ghost.CharacterMotor))
        {
            Stop(ghost, "body lost");
            return;
        }

        if (body.healthComponent == null || !body.healthComponent.alive)
        {
            Stop(ghost, "death");
            return;
        }

        if (!body.hasEffectiveAuthority)
        {
            Stop(ghost, "lost authority");
            return;
        }

        if (ghost.Machine == null || ghost.Machine.state != ghost.BodyState)
        {
            Stop(ghost, "dash state exited");
            return;
        }

        if (!ghost.Initialized && !InitializeGhost(ghost))
        {
            return;
        }

        RememberPredictedTravel(ghost);

        // The ghost's early movement is also sent to the server through the player's existing
        // CharacterNetworkTransform. Without the matching StartLead the server vehicle starts
        // at S0 and its seat would drag the server player back (others see the old rubber band),
        // so a ghost with no recorded lead must not run. StartLead records 0 for wall/cap cases.
        if (!ghost.LeadRecorded)
        {
            Stop(ghost, "StartLead did not report a start (disabled/failed); no ghost");
            return;
        }

        if (ghost.Handoff)
        {
            AdvanceHandoff(ghost);
            return;
        }

        double elapsed = Time.time - ghost.StartTime;
        if (!ghost.VehicleStateSeen && VehicleStateSeen(ghost))
        {
            ghost.VehicleStateSeen = true;
            Diag?.Info(
                $"{Tag}: vehicle state seen after {elapsed:F2}s (U={ghost.OneWay:F3}s); " +
                $"ghost=({ghost.Position.x:F2},{ghost.Position.z:F2}) " +
                $"vehicle={VehiclePositionText(body)} gap={VehicleGap(ghost):F2}m; " +
                $"traveled={ghost.TraveledSeconds:F2}s/{ghost.MotionSeconds:F2}s, still simulating.");
        }

        // The seat beats the deadline when both land on the same frame: the timeout exists
        // for "vehicle never arrived", not for "arrived at 3.00 s".
        // Local early end keeps the ghost driving through the seat window; handing off there
        // would freeze the body at the truncated position and leave the vanilla tail delay.
        if (body.currentVehicle != null && !EnforcerLocalEndPatch.OwnsGhost(body))
        {
            BeginHandoff(ghost, body.currentVehicle);
            if (ghost.Handoff)
            {
                AdvanceHandoff(ghost);
            }
            return;
        }

        // P10: the value cached at entry assumed the entry RTT; when the RTT spikes mid-dash
        // (log: 0.352 s -> 0.966 s) the cached 1.06 s fired before the late vehicle arrived.
        // Re-derive from the current RTT, never shrinking; a fixed override stays fixed.
        var timeout = ghost.TimeoutSecondsCached;
        if (!(fixedTimeoutSeconds?.Invoke() > 0f))
        {
            timeout = PredictionMath.ExtendStuckTimeout(
                timeout,
                TimeoutRttSeconds(),
                timeoutMaxSeconds?.Invoke() ?? (float)PredictionMath.MaxStuckTimeoutSeconds);
        }

        // A seated ghost kept by LocalEnd has its vehicle; a lengthened dash may outlast the timeout.
        if (body.currentVehicle == null && elapsed >= timeout)
        {
            Stop(
                ghost,
                $"timeout after {elapsed:F2}s (timeout {timeout:F2}s), vehicle never arrived");
            return;
        }

        if (MovedExternally(ghost))
        {
            Stop(ghost, "position moved externally while ghosting");
            return;
        }

        if (ghost.WallStopped || ghost.TraveledSeconds >= ghost.MotionSeconds - 1e-6)
        {
            if (!ghost.MotionDoneLogged)
            {
                ghost.MotionDoneLogged = true;
                Diag?.Info(
                    $"{Tag}: ghost motion done at t={elapsed:F2}s (motion={ghost.MotionSeconds:F2}s, " +
                    $"predicted={ghost.PredictedTravel:F2}m, wall={ghost.WallStopped}); holding for the seat.");
            }

            ApplyPosition(ghost, ghost.Position);
            return;
        }

        double stepSeconds = PredictionMath.GhostStepSeconds(
            deltaTime, ghost.TraveledSeconds, ghost.MotionSeconds);
        if (stepSeconds <= 0.0)
        {
            ApplyPosition(ghost, ghost.Position);
            return;
        }

        double before = ghost.TraveledSeconds;
        double after = before + stepSeconds;
        double leadPart = Overlap(before, after, 0.0, ghost.LeadSeconds);
        double dashStart = ghost.LeadSeconds;
        double dashEnd = ghost.LeadSeconds + ghost.Duration;
        double dashPart = Overlap(before, after, dashStart, dashEnd);
        double leadSpeed = ghost.LeadSeconds > 1e-6 ? ghost.LeadDistance / ghost.LeadSeconds : 0.0;
        double dashMid = Math.Max(dashStart, Math.Min(after, dashEnd));
        float dashT = Mathf.Clamp((float)((dashMid - dashStart) / ghost.Duration), 0f, 1f);
        float curveValue = ghost.Curve != null ? ghost.Curve.Evaluate(dashT) : 1f;
        if (dashPart > 0.0)
        {
            FollowSteering(ghost);
        }

        double dashSpeed = curveValue * ghost.SpeedCoefficient * ghost.DirectionScale;
        double desired = leadSpeed * leadPart + dashSpeed * dashPart;

        double wallDistance = double.PositiveInfinity;
        if (desired > 0.01)
        {
            wallDistance = EnforcerStartLeadPatch.RaycastDistance(
                ghost.Position + ghost.CoreOffset,
                ghost.Direction,
                (float)(desired + 0.01));
        }

        double limited = PredictionMath.GhostStepWallLimit(desired, wallDistance);
        if (!double.IsPositiveInfinity(wallDistance) && limited < desired - 1e-3)
        {
            ghost.WallStopped = true;
            Diag?.Info(
                $"{Tag}: wall stop at t={elapsed:F2}s wallDistance={wallDistance:F2}m " +
                $"desired={desired:F3}m applied={limited:F3}m pos=({ghost.Position.x:F2},{ghost.Position.z:F2}); " +
                "holding position until handoff/timeout.");
        }

        var next = ghost.Position + ghost.Direction * (float)limited;
        ApplyPosition(ghost, next);
        ghost.TraveledSeconds += stepSeconds;

        if (!ghost.LoggedFirstStep)
        {
            ghost.LoggedFirstStep = true;
            Diag?.Info(
                $"{Tag}: first step t={elapsed:F2}s curve={curveValue:F2} speed={dashSpeed:F2}m/s " +
                $"lead={leadSpeed * leadPart:F3}m dash={dashSpeed * dashPart:F3}m " +
                $"step={limited:F3}m pos=({next.x:F2},{next.z:F2}).");
        }
        else
        {
            Diag?.Throttled(
                $"{Tag}.step",
                $"{Tag}: ghost t={elapsed:F2}s curve={curveValue:F2} speed={dashSpeed:F2}m/s " +
                $"traveled={ghost.TraveledSeconds:F2}s/{ghost.MotionSeconds:F2}s " +
                $"pos=({next.x:F2},{next.z:F2}) U={ghost.OneWay:F3}s.");
        }
    }

    /// <summary>Length of the intersection of [start, end] with [low, high].</summary>
    private static double Overlap(double start, double end, double low, double high)
        => Math.Max(0.0, Math.Min(end, high) - Math.Max(start, low));

    private void BeginHandoff(GhostState ghost, VehicleSeat seat)
    {
        if (ghost.Handoff || seat == null || seat.seatPosition == null || ghost.Body == null)
        {
            return;
        }

        // Only our own local ghost may suppress this seat's snap.
        var passenger = seatPassengerField.GetValue(seat) as GameObject;
        if (passenger == null || passenger != ghost.Body.gameObject
            || !ghost.Body.hasEffectiveAuthority
            || ghost.Body.currentVehicle != seat)
        {
            return;
        }

        if (!ghost.Initialized && !InitializeGhost(ghost))
        {
            return;
        }

        var current = (Vector3)transientPositionProperty.GetValue(ghost.Motor);
        var target = seat.seatPosition.position;
        float gap = Vector3.Distance(current, target);
        double elapsed = Time.time - ghost.StartTime;
        double along = AlongTrack(current, target, ghost.Direction, out var lateral);

        if (gap <= MinHandoffMeters)
        {
            Stop(ghost, $"seat already at ghost, gap={gap:F2}m");
            return;
        }

        if (PredictionMath.HandoffIsTeleport(gap, ghost.PredictedTravel))
        {
            Diag?.Info(
                $"{Tag}: seat too far, gap={gap:F2}m > limit " +
                $"{PredictionMath.HandoffDistanceLimit(ghost.PredictedTravel):F1}m " +
                $"(predicted={ghost.PredictedTravel:F1}m); ghost ends, vanilla seat snap.");
            Stop(ghost, "seat too far (teleport/respawn)");
            return;
        }

        ghost.Handoff = true;
        ghost.Blending = false;
        ghost.Seat = seat;
        ghost.HoldFrom = current;
        ghost.HoldStartTime = Time.time;
        ghost.BlendFrom = current;
        ghost.LastApplied = current;
        ghost.HoldTimeoutSeconds = PredictionMath.HoldTimeout(
            TimeoutRttSeconds(),
            timeoutMaxSeconds?.Invoke() ?? (float)PredictionMath.MaxStuckTimeoutSeconds);

        Diag?.Info(
            $"{Tag}: handoff begin after {elapsed:F2}s (U={ghost.OneWay:F3}s) " +
            $"ghost=({current.x:F2},{current.z:F2}) seat=({target.x:F2},{target.z:F2}) " +
            $"gap={gap:F2}m along={along:F2}m lateral={lateral:F2}m " +
            $"traveled={ghost.TraveledSeconds:F2}s/{ghost.MotionSeconds:F2}s " +
            $"predicted={ghost.PredictedTravel:F2}m holdTimeout={ghost.HoldTimeoutSeconds:F2}s " +
            $"vehicleStateSeen={ghost.VehicleStateSeen} disablePassengerMotor={seat.disablePassengerMotor} " +
            $"vehicle={VehiclePositionText(ghost.Body)}.");

        if (PredictionMath.HandoffCaughtUp(along, lateral))
        {
            StartBlend(ghost, current, target);
        }
    }

    /// <summary>
    /// Drives the hold/blend from absolute time so it can be called from either CharacterMotor
    /// (before its update) or VehicleSeat.UpdatePassengerPosition (which still runs when
    /// disablePassengerMotor has disabled the passenger motor). Idempotent within a frame.
    /// </summary>
    private void AdvanceHandoff(GhostState ghost)
    {
        if (!ghost.Handoff)
        {
            return;
        }

        if (ghost.Body == null || ghost.Body.healthComponent == null || !ghost.Body.healthComponent.alive)
        {
            Stop(ghost, "death during handoff");
            return;
        }

        if (!ghost.Body.hasEffectiveAuthority)
        {
            Stop(ghost, "lost authority during handoff");
            return;
        }

        if (ghost.Seat == null || ghost.Seat.seatPosition == null || ghost.Body.currentVehicle != ghost.Seat)
        {
            // Ejection is vanilla VehicleShieldDashExit; ExitSmoothing owns that hard snap.
            Stop(ghost, "seat lost during handoff (ejection)");
            return;
        }

        var current = (Vector3)transientPositionProperty.GetValue(ghost.Motor);
        var target = ghost.Seat.seatPosition.position;
        float gap = Vector3.Distance(current, target);
        double along = AlongTrack(current, target, ghost.Direction, out var lateral);

        if (MovedExternally(ghost))
        {
            Stop(ghost, "position moved externally during handoff");
            return;
        }

        double now = Time.time;
        if (!ghost.Blending)
        {
            if (PredictionMath.HandoffCaughtUp(along, lateral))
            {
                StartBlend(ghost, current, target);
            }
            else
            {
                double held = now - ghost.HoldStartTime;
                if (held < ghost.HoldTimeoutSeconds)
                {
                    ApplyPosition(ghost, ghost.HoldFrom);
                    Diag?.Throttled(
                        $"{Tag}.hold",
                        $"{Tag}: holding for seat {held:F2}s/{ghost.HoldTimeoutSeconds:F2}s " +
                        $"gap={gap:F2}m along={along:F2}m lateral={lateral:F2}m " +
                        $"seat=({target.x:F2},{target.z:F2}) ghost=({current.x:F2},{current.z:F2}).");
                    return;
                }

                if (PredictionMath.HandoffIsTeleport(gap, ghost.PredictedTravel))
                {
                    Diag?.Info(
                        $"{Tag}: hold timeout {held:F2}s, gap={gap:F2}m too far to blend " +
                        $"(limit {PredictionMath.HandoffDistanceLimit(ghost.PredictedTravel):F1}m); vanilla seat snap.");
                    Stop(ghost, "hold timeout, target too far");
                    return;
                }

                Diag?.Info(
                    $"{Tag}: hold timeout {held:F2}s, blending to seat anyway " +
                    $"(gap={gap:F2}m along={along:F2}m lateral={lateral:F2}m).");
                StartBlend(ghost, current, target);
            }
        }

        if (ghost.Blending)
        {
            if (PredictionMath.HandoffIsTeleport(gap, ghost.PredictedTravel))
            {
                Stop(ghost, $"blend target jumped to {gap:F2}m (teleport/respawn)");
                return;
            }

            double blendElapsed = now - ghost.BlendStartTime;
            double alpha = PredictionMath.InterpolationAlpha(blendElapsed, ghost.BlendDuration);
            var next = Vector3.Lerp(ghost.BlendFrom, target, (float)alpha);
            ApplyPosition(ghost, next);
            if (alpha >= 1.0)
            {
                Diag?.Info(
                    $"{Tag}: handoff done in {blendElapsed:F2}s (blend {ghost.BlendDuration:F2}s) " +
                    $"residual={Vector3.Distance(next, target):F2}m; vanilla seat snap resumes.");
                Stop(ghost, "handoff complete");
            }
        }
    }

    private void StartBlend(GhostState ghost, Vector3 from, Vector3 target)
    {
        ghost.Blending = true;
        ghost.BlendFrom = from;
        ghost.BlendStartTime = Time.time;
        ghost.BlendDuration = (float)PredictionMath.ClampInterpolationDuration(ghost.OneWay);
        Diag?.Info(
            $"{Tag}: caught up after {(Time.time - ghost.HoldStartTime):F2}s; blend to seat " +
            $"from=({from.x:F2},{from.z:F2}) to=({target.x:F2},{target.z:F2}) " +
            $"gap={Vector3.Distance(from, target):F2}m duration={ghost.BlendDuration:F2}s.");
    }

    private bool ShouldSkipSeatSnap(VehicleSeat seat)
    {
        if (seat == null)
        {
            return false;
        }

        var passenger = seatPassengerField.GetValue(seat) as GameObject;
        var body = passenger != null ? passenger.GetComponent<CharacterBody>() : null;
        var motor = body != null ? body.characterMotor : null;
        if (motor is null || !states.TryGetValue(motor, out var ghost) || ghost.Body != body)
        {
            return false;
        }

        if (!ShouldRun())
        {
            Stop(ghost, "config off / host / tripped during handoff");
            return false;
        }

        if (ghost.Handoff && ghost.Seat != seat)
        {
            Stop(ghost, "seat changed during handoff");
            return false;
        }

        if (!ghost.Handoff)
        {
            if (ghost.Machine == null || ghost.Machine.state != ghost.BodyState)
            {
                Stop(ghost, "stale ghost, dash state already exited");
                return false;
            }

            BeginHandoff(ghost, seat);
        }

        // Drive the blend from here too: with disablePassengerMotor the passenger motor is
        // disabled, so CharacterMotor.BeforeCharacterUpdate never advances it.
        AdvanceHandoff(ghost);
        return ghost.Handoff && ghost.Seat == seat;
    }

    /// <summary>Along-dash and lateral components of (target - from) on the horizontal plane.</summary>
    private static double AlongTrack(Vector3 from, Vector3 to, Vector3 direction, out double lateral)
    {
        var delta = to - from;
        delta.y = 0f;
        float along = Vector3.Dot(delta, direction);
        lateral = (delta - direction * along).magnitude;
        return along;
    }

    private static bool VehicleStateSeen(GhostState ghost)
    {
        try
        {
            return ghost.VehicleEnteredField != null
                   && ghost.VehicleEnteredField.GetValue(ghost.BodyState) is bool entered
                   && entered;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool MovedExternally(GhostState ghost)
    {
        var current = (Vector3)transientPositionProperty.GetValue(ghost.Motor);
        return Vector3.Distance(current, ghost.LastApplied) > TeleportSlackMeters;
    }

    private static void ApplyPosition(GhostState ghost, Vector3 position)
    {
        setPositionMethod.Invoke(ghost.Motor, new object[] { position, true });
        ghost.Position = position;
        ghost.LastApplied = position;
    }

    private double TimeoutSeconds(double rtt)
    {
        // Keep the ghost's timeout in step with EnforcerFeedback: same fixed override,
        // same automatic cap; a ghost that quits earlier would sit idle until the fallback.
        var fixedSeconds = fixedTimeoutSeconds?.Invoke() ?? 0f;
        return fixedSeconds > 0f
            ? fixedSeconds
            : PredictionMath.StuckTimeout(
                rtt,
                timeoutMaxSeconds?.Invoke() ?? (float)PredictionMath.MaxStuckTimeoutSeconds);
    }

    /// <summary>
    /// Fallback RTT may use the larger of game and measured: a spike must not fire the
    /// timeout before the late vehicle arrives (the 3x/1 s formula is applied by the caller).
    /// </summary>
    private double TimeoutRttSeconds()
        => Latency != null && Latency.TryGetTimeoutRoundTrip(out var rtt) ? rtt : 0.0;

    private void Stop(GhostState ghost, string reason)
    {
        RememberPredictedTravel(ghost);
        var wasHandoff = ghost.Handoff;
        var blendElapsed = ghost.Blending ? Time.time - ghost.BlendStartTime : 0.0;
        ghost.Handoff = false;
        ghost.Blending = false;
        states.Remove(ghost.CharacterMotor);
        activeStates = Math.Max(0, activeStates - 1);
        Diag?.Info(
            $"{Tag}: ghost stopped: {reason}; pos=({ghost.Position.x:F2},{ghost.Position.z:F2}) " +
            $"elapsed={(Time.time - ghost.StartTime):F2}s handoff={wasHandoff} " +
            $"blendElapsed={blendElapsed:F2}s traveled={ghost.TraveledSeconds:F2}s/" +
            $"{ghost.MotionSeconds:F2}s predicted={ghost.PredictedTravel:F2}m.");
    }

    private AnimationCurve GetCurve(Type vehicleStateType, out string source)
    {
        if (curvesByVehicleType.TryGetValue(vehicleStateType, out var cached))
        {
            source = curveSourceByVehicleType[vehicleStateType];
            return cached;
        }

        AnimationCurve curve = null;
        var curveField = vehicleStateType.GetField(
            "forwardVelocityCurve", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        try
        {
            var state = (EntityState)Activator.CreateInstance(vehicleStateType);
            curve = curveField != null ? curveField.GetValue(state) as AnimationCurve : null;
            source = curve != null
                ? $"{vehicleStateType.Name}.forwardVelocityCurve"
                : $"{vehicleStateType.Name} curve missing -> constant 1.0";
        }
        catch (Exception exception)
        {
            source = $"{vehicleStateType.Name} curve unavailable ({exception.GetType().Name}) -> constant 1.0";
        }

        curvesByVehicleType[vehicleStateType] = curve;
        curveSourceByVehicleType[vehicleStateType] = source;
        return curve;
    }

    // Diagnostics only: must never be able to trip the patch.
    private static string VehiclePositionText(CharacterBody body)
    {
        try
        {
            var vehicle = FindVehicle(body);
            if (vehicle == null)
            {
                return "none";
            }

            var position = vehicle.transform.position;
            return $"({position.x:F2},{position.y:F2},{position.z:F2})";
        }
        catch (Exception)
        {
            return "error";
        }
    }

    private static float VehicleGap(GhostState ghost)
    {
        try
        {
            var vehicle = FindVehicle(ghost.Body);
            return vehicle != null ? Vector3.Distance(ghost.Position, vehicle.transform.position) : -1f;
        }
        catch (Exception)
        {
            return -1f;
        }
    }

    private static GameObject FindVehicle(CharacterBody body)
    {
        var controller = body != null ? body.GetComponent<EnforcerVehicleDashController>() : null;
        return controller != null && controllerVehicleProperty != null
            ? controllerVehicleProperty.GetValue(controller) as GameObject
            : null;
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
