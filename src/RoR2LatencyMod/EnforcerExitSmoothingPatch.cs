// 票09 EnforcerExitSmoothing：下冲刺退场时位置不再前跳 2–3m。
//
// 反编译核对（F:/ror2-decomp/RoR2src，只读）：
// - 退场状态 EntityStates.Enforcer/VehicleShieldDashExit.cs:124-144 private void
//   OnPassengerExitAuthority(GameObject)：:138 `characterMotor.Motor.SetPosition(vehicleAuthorityPosition)`
//   把本机角色瞬间贴到主机权威退场位置。vehicleAuthorityPosition 在服务端 :146-160 OnEnterServer :148
//   取当时载具 transform.position，客户端 :62-76 OnDeserialize :67 读回。
// - 客户端调用路径：:78-122 OnEnter 在 passengerBody.hasEffectiveAuthority 时订阅
//   vehicleSeat.onPassengerExit（:96），若座位已空则以 null 立即调用（:100）。座位退场来自
//   服务端本状态 :146-160 OnEnterServer :155 vehicleSeat.EjectPassenger()
//   → RoR2/VehicleSeat.cs:578-590 SetPassenger(null)
//   → 客户端 SyncVar hook :408-428 SetPassenger → :391-406 SetPassengerInternal → :780-917
//   OnPassengerExit → :915 onPassengerExit 事件。服务端退场位置比客户端当前渲染位置靠前
//   U+68ms 的冲刺位移，:138 的硬贴就是退场前跳。
//   （入场/乘坐期另一处硬贴是 VehicleSeat.cs:511-538 UpdatePassengerPosition :527，不属本票。）
// - 本补丁：prefix 记录硬贴前的 Motor.TransientPosition，postfix 保留原版其余逻辑
//   （速度/BaseVelocity/SetTransmitUpdatesAuthority/PlayExitAnimation）后把位置放回起点，
//   再用 0.1–0.2s（PredictionMath.ClampInterpolationDuration/InterpolationAlpha）在
//   CharacterMotor.BeforeCharacterUpdate（KCC 每步移动前）逐帧 lerp 到目标。
//   只改本地位置呈现；输入与 grounded 检查由 KCC 在插值后的位置正常计算，
//   不改伤害/命中，不新增网络消息。安全阀：距离超过最后一次冲刺的预测里程裕量（无预测时
//   退回 10m）判为传送/重生走原版硬贴；新载具/死亡/失去权威/位置被外部移动时立即放弃并
//   释放控制；开关关闭或熔断走原版。

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using EntityStates.Enforcer;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace RoR2LatencyMod;

public sealed class EnforcerExitSmoothingPatch : SkillPatch
{
    private const string Tag = "EnforcerExitSmoothing";

    /// <summary>Below this the hard snap is invisible; keep vanilla.</summary>
    private const float MinSmoothMeters = 0.05f;

    /// <summary>Teleport limit when no recent ghost prediction exists; hard snap above it.</summary>
    private const float MaxSmoothMeters = 10f;

    /// <summary>Slack for one simulation step before an external position change counts as a teleport.</summary>
    private const float TeleportSlackMeters = 2.5f;

    private static EnforcerExitSmoothingPatch instance;
    private static FieldInfo passengerBodyField;
    private static PropertyInfo motorProperty;
    private static PropertyInfo transientPositionProperty;
    private static MethodInfo setPositionMethod;

    // CharacterMotor derives from KinematicCharacterController.BaseCharacterController (not referenced);
    // read its own public velocity field by reflection instead of touching the base type.
    private static FieldInfo characterMotorVelocityField;

    // Weak keys: a body destroyed mid-smoothing must not stay referenced.
    private readonly ConditionalWeakTable<CharacterMotor, SmoothState> states =
        new ConditionalWeakTable<CharacterMotor, SmoothState>();

    private int activeCount;

    public EnforcerExitSmoothingPatch(
        Func<bool> isEnabled,
        Func<float> exitSmoothSeconds,
        DiagLog diag = null,
        LatencyEstimator latency = null)
        : base("EnforcerExitSmoothing", PatchSide.Client, isEnabled, diag, latency)
    {
        ExitSmoothSeconds = exitSmoothSeconds;
    }

    public Func<float> ExitSmoothSeconds { get; }

    protected override void Install()
    {
        passengerBodyField = AccessTools.Field(typeof(VehicleShieldDashExit), "passengerBody");
        motorProperty = typeof(CharacterMotor).GetProperty("Motor");
        characterMotorVelocityField =
            typeof(CharacterMotor).GetField("velocity", BindingFlags.Instance | BindingFlags.Public);
        var exitMethod = AccessTools.Method(
            typeof(VehicleShieldDashExit), "OnPassengerExitAuthority", new[] { typeof(GameObject) });
        var beforeCharacterUpdate = AccessTools.Method(
            typeof(CharacterMotor), "BeforeCharacterUpdate", new[] { typeof(float) });
        if (passengerBodyField == null || motorProperty == null
            || characterMotorVelocityField == null
            || exitMethod == null || beforeCharacterUpdate == null)
        {
            throw new MissingMemberException(
                "EnforcerExitSmoothing members missing: " +
                $"passengerBody={passengerBodyField != null}, Motor={motorProperty != null}, " +
                $"velocity={characterMotorVelocityField != null}, " +
                $"OnPassengerExitAuthority={exitMethod != null}, " +
                $"BeforeCharacterUpdate={beforeCharacterUpdate != null}");
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

        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            exitMethod,
            prefix: new HarmonyMethod(
                typeof(EnforcerExitSmoothingPatch), nameof(OnPassengerExitAuthorityPrefix)),
            postfix: new HarmonyMethod(
                typeof(EnforcerExitSmoothingPatch), nameof(OnPassengerExitAuthorityPostfix)));
        harmony.Patch(
            beforeCharacterUpdate,
            postfix: new HarmonyMethod(
                typeof(EnforcerExitSmoothingPatch), nameof(BeforeCharacterUpdatePostfix)));

        instance = this;
        Diag?.Info(
            $"{Tag}: installed. 退场跳变来源 VehicleShieldDashExit.cs:124-144（private " +
            "OnPassengerExitAuthority，:138 Motor.SetPosition(vehicleAuthorityPosition) 硬贴；" +
            "客户端 :78-122 OnEnter 订阅座位 :915 onPassengerExit）→ 改为 0.1–0.2s 本地插值。");
    }

    private static void OnPassengerExitAuthorityPrefix(
        VehicleShieldDashExit __instance, out SmoothState __state)
    {
        __state = null;
        var self = instance;
        if (self == null)
        {
            return;
        }

        SmoothState captured = null;
        self.Guard(() => captured = self.CaptureExit(__instance));
        __state = captured;
    }

    private static void OnPassengerExitAuthorityPostfix(SmoothState __state)
    {
        var self = instance;
        if (self == null || __state == null)
        {
            return;
        }

        self.Guard(() => self.BeginSmoothingSafe(__state));
    }

    /// <summary>If starting the lerp throws, leave the vanilla (target) position, never Start.</summary>
    private void BeginSmoothingSafe(SmoothState state)
    {
        try
        {
            BeginSmoothing(state);
        }
        catch
        {
            RemoveState(state);
            try
            {
                SetMotorPosition(state.Motor, state.Target);
            }
            catch
            {
                // Motor already gone.
            }

            throw;
        }
    }

    private static void BeforeCharacterUpdatePostfix(CharacterMotor __instance, float deltaTime)
    {
        var self = instance;
        if (self == null || self.activeCount == 0)
        {
            return;
        }

        if (!self.states.TryGetValue(__instance, out var state))
        {
            return;
        }

        self.Guard(() => self.StepSafe(state, deltaTime));
    }

    /// <summary>
    /// Releases local control even if a step throws: the guard trips afterwards, so no later
    /// step would ever run and the body would stay parked mid-interpolation.
    /// </summary>
    private void StepSafe(SmoothState state, float deltaTime)
    {
        try
        {
            Step(state, deltaTime);
        }
        catch
        {
            RemoveState(state);
            try
            {
                SetMotorPosition(state.Motor, state.Target);
            }
            catch
            {
                // Motor already gone; nothing left to restore.
            }

            throw;
        }
    }

    private SmoothState CaptureExit(VehicleShieldDashExit exitState)
    {
        if (exitState == null || !ShouldRun())
        {
            return null;
        }

        if (!(passengerBodyField.GetValue(exitState) is CharacterBody body)
            || !body.hasEffectiveAuthority
            || body.characterMotor is null
            || body.healthComponent == null
            || !body.healthComponent.alive
            || body.currentVehicle != null)
        {
            return null;
        }

        // P9: local early end skipped the vanilla hard snap; smoothing here would drag the
        // player to the server exit position anyway.
        if (EnforcerLocalEndPatch.SuppressesExitSmoothing(exitState))
        {
            return null;
        }

        double requested = ExitSmoothSeconds != null ? ExitSmoothSeconds() : 0.0;
        if (requested <= 0.0)
        {
            return null;
        }

        object motor = motorProperty.GetValue(body.characterMotor);
        if (motor == null)
        {
            return null;
        }

        var target = exitState.vehicleAuthorityPosition;
        var start = (Vector3)transientPositionProperty.GetValue(motor);
        float distance = Vector3.Distance(start, target);
        if (distance <= MinSmoothMeters)
        {
            return null;
        }

        // P10: a fast dash's own drift can exceed the fixed 10 m (16.30 / 13.23 m in the log),
        // so judge against the last dash's predicted travel when the ghost supplied one.
        double predictedTravel = EnforcerGhostDashPatch.RecentPredictedTravelMeters();
        if (PredictionMath.ExitSmoothingIsTeleport(distance, predictedTravel, MaxSmoothMeters))
        {
            Diag?.Info(
                $"{Tag}: hard snap, distance={distance:F2}m, predicted={predictedTravel:F2}m, " +
                $"limit={(predictedTravel > 0.0 ? PredictionMath.HandoffDistanceLimit(predictedTravel) : MaxSmoothMeters):F1}m " +
                "(teleport/respawn, not dash-end drift)");
            return null;
        }

        double oneWay = 0.0;
        if (Latency != null && Latency.TryGetOneWayLatency(out var u))
        {
            oneWay = u;
        }

        return new SmoothState
        {
            Body = body,
            CharacterMotor = body.characterMotor,
            Motor = motor,
            Start = start,
            Target = target,
            Duration = (float)PredictionMath.ClampInterpolationDuration(requested),
            LastApplied = start,
            WallStart = Time.time,
            OneWay = oneWay,
        };
    }

    private void BeginSmoothing(SmoothState state)
    {
        // The prefix only hands over when the vanilla body would have snapped; re-check the
        // cheap conditions so a mid-call change leaves the vanilla hard snap alone.
        var body = state.Body;
        if (body == null
            || !body.hasEffectiveAuthority
            || body.characterMotor is null
            || body.currentVehicle != null)
        {
            return;
        }

        SetMotorPosition(state.Motor, state.Start); // undo the vanilla snap, now interpolate to it

        RemoveState(state); // a leftover smooth from the previous exit must not fight this one
        states.Add(state.CharacterMotor, state);
        activeCount++;

        Diag?.Info(
            $"{Tag}: start distance={Vector3.Distance(state.Start, state.Target):F2}m " +
            $"duration={state.Duration:F2}s U={state.OneWay:F3}s " +
            $"start=({state.Start.x:F2},{state.Start.y:F2},{state.Start.z:F2}) " +
            $"target=({state.Target.x:F2},{state.Target.y:F2},{state.Target.z:F2})");
    }

    private void Step(SmoothState state, float deltaTime)
    {
        if (deltaTime <= 0f)
        {
            return;
        }

        // Release even if no simulation step ran while the body was parked (e.g. torn off the
        // seat by something other than a normal exit); stale control must not linger.
        if (Time.time - state.WallStart > state.Duration + 1f)
        {
            RemoveState(state);
            Diag?.Info($"{Tag}: abort reason=stale at elapsed={state.Elapsed:F3}s");
            return;
        }

        if (!ShouldRun())
        {
            // Switched off mid-smooth: apply the vanilla end position, then behave as vanilla.
            RemoveState(state);
            SetMotorPosition(state.Motor, state.Target);
            Diag?.Info($"{Tag}: abort reason=disabled at elapsed={state.Elapsed:F3}s (snapped to target)");
            return;
        }

        var body = state.Body;
        if (body == null)
        {
            RemoveState(state);
            Diag?.Info($"{Tag}: abort reason=destroyed at elapsed={state.Elapsed:F3}s");
            return;
        }

        if (!body.hasEffectiveAuthority)
        {
            RemoveState(state);
            Diag?.Info($"{Tag}: abort reason=authority-lost at elapsed={state.Elapsed:F3}s");
            return;
        }

        if (body.healthComponent == null || !body.healthComponent.alive)
        {
            RemoveState(state);
            Diag?.Info($"{Tag}: abort reason=death at elapsed={state.Elapsed:F3}s");
            return;
        }

        if (body.currentVehicle != null)
        {
            RemoveState(state);
            Diag?.Info($"{Tag}: abort reason=new-vehicle at elapsed={state.Elapsed:F3}s");
            return;
        }

        // Someone else moved the body (teleport); the recorded target is stale, let them own it.
        var current = GetMotorPosition(state.Motor);
        float moved = (current - state.LastApplied).magnitude;
        var velocity = (Vector3)characterMotorVelocityField.GetValue(state.CharacterMotor);
        float allowed = TeleportSlackMeters + velocity.magnitude * deltaTime;
        if (moved > allowed)
        {
            RemoveState(state);
            Diag?.Info($"{Tag}: abort reason=teleport moved={moved:F2}m at elapsed={state.Elapsed:F3}s");
            return;
        }

        state.Elapsed += deltaTime;
        if (state.Elapsed >= state.Duration)
        {
            RemoveState(state);
            SetMotorPosition(state.Motor, state.Target);
            Diag?.Info(
                $"{Tag}: done elapsed={state.Elapsed:F3}s duration={state.Duration:F2}s " +
                $"distance={Vector3.Distance(state.Start, state.Target):F2}m");
            return;
        }

        double alpha = PredictionMath.InterpolationAlpha(state.Elapsed, state.Duration);
        var position = Vector3.LerpUnclamped(state.Start, state.Target, (float)alpha);
        SetMotorPosition(state.Motor, position);
        state.LastApplied = position;
    }

    private void RemoveState(SmoothState state)
    {
        if (states.Remove(state.CharacterMotor))
        {
            activeCount--;
        }
    }

    private static Vector3 GetMotorPosition(object motor)
        => (Vector3)transientPositionProperty.GetValue(motor);

    private static void SetMotorPosition(object motor, Vector3 position)
        => setPositionMethod.Invoke(motor, new object[] { position, true });

    private sealed class SmoothState
    {
        public CharacterBody Body;
        public CharacterMotor CharacterMotor;
        public object Motor;
        public Vector3 Start;
        public Vector3 Target;
        public float Duration;
        public float Elapsed;
        public Vector3 LastApplied;
        public float WallStart;
        public double OneWay;
    }
}
