// 票08 EnforcerStartLead：冲刺起点前推，缩小 Host 载具到达后的回拉。
//
// 反编译核对（F:/ror2-decomp/RoR2src）：
// - 松键上报点：EntityStates.Enforcer/ProxyShieldDash.cs:110-117 OnSerialize 写 private positionFromAuthority
//   （:68）；该值在 :143 由本机 `base.transform.position` 填充。客户端状态变化经
//   RoR2/EntityStateMachine.cs:174-193 SetState → NetworkStateMachine.cs:119 SendSetEntityState 发出，
//   服务端 RoR2/NetworkStateMachine.cs:79 HandleSetEntityState 读取（:88-99 只做归属校验）→
//   ProxyShieldDash.cs:119 OnDeserialize 还原 → EnforcerVehicleDashController.cs:116 ActivateVehicle
//   直接把载具摆到该位置。全路径无范围校验/拒绝/回滚（详见 docs/analysis-2026-10-22.md 追加小节）。
// - 因此本补丁只在发送前把 positionFromAuthority 沿冲刺方向前推：速度 × U（U = RTT/2），
//   纯函数 PredictionMath.StartLeadWithWall（PredictionMath.cs:74-80，0.25s 位移封顶 + 0.5m 墙距余量）。
//   速度取本机角色当前速度在水平冲刺方向上的投影（延迟期间本机实际会走到的距离）。
// - 挂点选非泛型的 NetworkStateMachine.SendSetEntityState（msg48 序列化的唯一入口）而不是
//   ProxyShieldDash<T>.OnSerialize：Heavy/Light 解析出的 MethodInfo 元数据相同但属于不同构造泛型，
//   Harmony 重复 Patch 会叠加执行（PatchInfo.Add 不去重），会把前推加两次。这里只挂一次。
// - 墙检测用运行期反射调用 UnityEngine.Physics（csproj 未引用 UnityEngine.PhysicsModule，不能新增引用）。
// - P7 交互（2026-10-23 修正）：幽灵拥有该冲刺时，本补丁改用幽灵的起点/冲刺方向/冲刺初速
//   计算 lead（同一个 StartLeadWithWall：0.25s 时间封顶、StartLeadMaxMeters、0.5m 墙距），
//   把上报起点写成 S0 + 冲刺方向 × lead，并调用 SetGhostLead 让幽灵按同一 lead 模拟。
//   U ≤ 0.25s 时幽灵轨迹与服务器载具在同一真实时刻重合，交接时无需快进；U 更大时 lead 封顶，
//   幽灵先到终点等待服务器载具（空间终点相同，时间上幽灵更早）。只在这里计算一次 lead，
//   避免「幽灵前移 + 起点前推」双重补偿。GhostDash 关闭/未参与/熔断时
//   TryGetGhostLeadInputs 为 false，保持原行为（本机速度投影×U + 墙距缩短）。
//   上报后额外调一次 ActivateVehicle(上报起点) 只改本机载具呈现，使其第一帧与服务器起点一致。
// 仅 Remote Client 生效；不改伤害/命中，不新增网络消息。

using System;
using System.Collections.Generic;
using System.Reflection;
using EntityStates;
using EntityStates.Enforcer;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace RoR2LatencyMod;

public sealed class EnforcerStartLeadPatch : SkillPatch
{
    private const string Tag = "EnforcerStartLead";

    private static EnforcerStartLeadPatch instance;
    private static MethodInfo raycastMethod;
    private static PropertyInfo raycastDistanceProperty;
    private static FieldInfo stateMachinesField;

    // CharacterMotor derives from KinematicCharacterController.BaseCharacterController, which the csproj
    // does not reference; read the public velocity field by reflection instead of touching the base type.
    private static readonly FieldInfo characterMotorVelocityField =
        typeof(CharacterMotor).GetField("velocity", BindingFlags.Instance | BindingFlags.Public);

    private readonly Dictionary<Type, FieldInfo> positionFromAuthorityByType = new Dictionary<Type, FieldInfo>();

    public EnforcerStartLeadPatch(
        Func<bool> isEnabled,
        Func<float> startLeadCapSeconds,
        Func<float> startLeadMaxMeters,
        DiagLog diag = null,
        LatencyEstimator latency = null)
        : base("EnforcerStartLead", PatchSide.Client, isEnabled, diag, latency)
    {
        StartLeadCapSeconds = startLeadCapSeconds;
        StartLeadMaxMeters = startLeadMaxMeters;
    }

    public Func<float> StartLeadCapSeconds { get; }

    /// <summary>0 = only the seconds cap applies.</summary>
    public Func<float> StartLeadMaxMeters { get; }

    protected override void Install()
    {
        if (characterMotorVelocityField == null)
        {
            throw new MissingMemberException("RoR2.CharacterMotor.velocity not found");
        }

        ResolveRaycast();
        stateMachinesField = AccessTools.Field(typeof(NetworkStateMachine), "stateMachines");
        var sendSetEntityState = AccessTools.Method(
            typeof(NetworkStateMachine), "SendSetEntityState", new[] { typeof(int) });
        if (stateMachinesField == null || sendSetEntityState == null)
        {
            throw new MissingMemberException(
                $"NetworkStateMachine members missing: stateMachines={stateMachinesField != null}, " +
                $"SendSetEntityState={sendSetEntityState != null}");
        }

        foreach (var type in new[] { typeof(ProxyShieldDashHeavy), typeof(ProxyShieldDashLight) })
        {
            var positionField = FindField(type, "positionFromAuthority");
            if (positionField == null)
            {
                throw new MissingMemberException($"{type.FullName} positionFromAuthority not found");
            }

            positionFromAuthorityByType[type] = positionField;
        }

        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            sendSetEntityState,
            prefix: new HarmonyMethod(typeof(EnforcerStartLeadPatch), nameof(SendSetEntityStatePrefix)));

        instance = this;
        Diag?.Info(
            $"{Tag}: installed (lead applied in NetworkStateMachine.SendSetEntityState, once per msg48). " +
            "HandleSetEntityState 全路径核对结论：服务端不校验上报的 positionFromAuthority" +
            "（NetworkStateMachine.cs:79-110 只查归属 → ProxyShieldDash.cs:119-126 OnDeserialize → " +
            "EntityStateMachine.cs:174-193 SetState → ProxyShieldDash.cs:128 OnEnter → " +
            "EnforcerVehicleDashController.cs:116-126 ActivateVehicle，无范围检查/拒绝/回滚）→ 启用起点前推。");
    }

    private static void SendSetEntityStatePrefix(NetworkStateMachine __instance, int stateMachineIndex)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        self.Guard(() => self.ApplyLeadBeforeSend(__instance, stateMachineIndex));
    }

    private void ApplyLeadBeforeSend(NetworkStateMachine networker, int stateMachineIndex)
    {
        var machines = stateMachinesField != null
            ? stateMachinesField.GetValue(networker) as EntityStateMachine[]
            : null;
        if (machines == null || stateMachineIndex < 0 || stateMachineIndex >= machines.Length)
        {
            return;
        }

        var machine = machines[stateMachineIndex];
        if (machine != null && machine.state != null)
        {
            ApplyLead(machine.state);
        }
    }

    private void ApplyLead(EntityState state)
    {
        if (!ShouldRun() || !IsLocalAuthority(state))
        {
            return;
        }

        if (!positionFromAuthorityByType.TryGetValue(state.GetType(), out var positionField))
        {
            return;
        }

        if (Latency == null || !Latency.TryGetOneWayLatency(out var oneWay) || oneWay <= 0.0)
        {
            return;
        }

        var components = state.outer.commonComponents;

        // P7: a ghost-owned dash reports the same predicted start it simulates (S0 + dash
        // speed * U), so ghost and server vehicle coincide in real time and the handoff has
        // no double compensation. Without a ghost the vanilla velocity projection is used.
        bool ghostOwns = EnforcerGhostDashPatch.TryGetGhostLeadInputs(
            state, out var ghostOrigin, out var ghostDirection, out var ghostSpeed);

        Vector3 direction;
        double speed;
        Vector3 raycastOrigin;
        if (ghostOwns)
        {
            direction = ghostDirection;
            speed = ghostSpeed;
            raycastOrigin = components.characterBody != null
                ? components.characterBody.corePosition
                : ghostOrigin;
        }
        else
        {
            direction = components.characterDirection != null
                ? components.characterDirection.forward
                : state.outer.transform.forward;
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f)
            {
                return;
            }

            direction.Normalize();
            object motor = components.characterMotor;
            var velocity = motor != null ? (Vector3)characterMotorVelocityField.GetValue(motor) : Vector3.zero;
            speed = Math.Max(0.0, Vector3.Dot(velocity, direction));
            raycastOrigin = components.characterBody != null
                ? components.characterBody.corePosition
                : state.outer.transform.position;
            if (speed <= 0.01)
            {
                Diag?.Throttled(
                    $"{Tag}.nospeed",
                    $"{Tag}: no lead, dash-direction speed={speed:F2}m/s " +
                    $"(motor velocity=({velocity.x:F2},{velocity.y:F2},{velocity.z:F2})), U={oneWay:F3}s.");
                return;
            }
        }

        if (speed <= 0.01)
        {
            Diag?.Throttled(
                $"{Tag}.ghostnospeed",
                $"{Tag}: ghost owns the dash but initial dash speed={speed:F2}m/s; no lead.");
            EnforcerGhostDashPatch.SetGhostLead(state, 0.0, 0.0);
            return;
        }

        double capSeconds = Math.Min(
            Math.Max(0f, StartLeadCapSeconds?.Invoke() ?? 0f),
            PredictionMath.StartLeadCapSeconds);
        double leadSeconds = Math.Min(Math.Max(0.0, oneWay), capSeconds);
        double rawLead = PredictionMath.StartLead(speed, leadSeconds);

        double wallDistance = rawLead > 0.01
            ? RaycastDistance(raycastOrigin, direction, (float)rawLead)
            : double.PositiveInfinity;

        double lead = PredictionMath.StartLeadWithWall(speed, leadSeconds, wallDistance);
        bool wallShortened = lead < rawLead - 1e-4;
        double hardCap = StartLeadMaxMeters?.Invoke() ?? 0f;
        bool hardCapHit = hardCap > 0f && lead > hardCap;
        if (hardCapHit)
        {
            lead = hardCap;
        }

        if (lead <= 0.001)
        {
            Diag?.Throttled(
                $"{Tag}.zero",
                $"{Tag}: no lead after wall/cap (raw={rawLead:F2}m, wallDistance=" +
                $"{(double.IsPositiveInfinity(wallDistance) ? "-" : wallDistance.ToString("F2"))}, " +
                $"capSeconds={capSeconds:F2}, maxMeters={hardCap:F2}, U={oneWay:F3}s, ghost={ghostOwns}).");
            if (ghostOwns)
            {
                EnforcerGhostDashPatch.SetGhostLead(state, 0.0, 0.0);
            }

            return;
        }

        var position = (Vector3)positionField.GetValue(state);
        var reportedStart = ghostOwns ? ghostOrigin + direction * (float)lead : position + direction * (float)lead;
        positionField.SetValue(state, reportedStart);

        if (ghostOwns)
        {
            // Single point of compensation: the ghost simulates this exact lead, and the local
            // vehicle is pre-placed here so the first seat frame matches the server-reported start.
            EnforcerGhostDashPatch.SetGhostLead(state, leadSeconds, lead);
            var controller = state.outer.GetComponent<EnforcerVehicleDashController>();
            if (controller != null)
            {
                controller.ActivateVehicle(reportedStart);
            }
        }

        bool cappedByTime = oneWay > leadSeconds + 1e-6;
        bool wallHit = !double.IsPositiveInfinity(wallDistance);
        Diag?.Info(
            $"{Tag}: lead={lead:F2}m speed={speed:F2}m/s dir=({direction.x:F2},{direction.z:F2}) " +
            $"U={oneWay:F3}s {Latency.Describe()} cap={capSeconds:F2}s capped={cappedByTime} " +
            $"wallHit={wallHit} wallDistance={(wallHit ? wallDistance.ToString("F2") : "-")} " +
            $"wallShortened={wallShortened} hardCapHit={hardCapHit} ghost={ghostOwns} " +
            $"reported=({reportedStart.x:F2},{reportedStart.z:F2})");
    }

    internal static void ResolveRaycast()
    {
        var physicsType = AccessTools.TypeByName("UnityEngine.Physics");
        var hitType = AccessTools.TypeByName("UnityEngine.RaycastHit");
        if (physicsType == null || hitType == null)
        {
            throw new MissingMemberException(
                $"UnityEngine.Physics/RaycastHit not found: physics={physicsType != null}, hit={hitType != null}");
        }

        raycastMethod = physicsType.GetMethod(
            "Raycast",
            new[] { typeof(Vector3), typeof(Vector3), hitType.MakeByRefType(), typeof(float), typeof(int) });
        raycastDistanceProperty = hitType.GetProperty("distance");
        if (raycastMethod == null || raycastDistanceProperty == null)
        {
            throw new MissingMemberException(
                $"Physics.Raycast(Vector3, Vector3, out RaycastHit, float, int) not found: " +
                $"method={raycastMethod != null}, distance={raycastDistanceProperty != null}");
        }
    }

    /// <summary>Reflection keeps the mod free of a UnityEngine.PhysicsModule reference; &lt;0 means no wall.</summary>
    internal static double RaycastDistance(Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (raycastMethod == null)
        {
            ResolveRaycast();
        }

        var args = new object[] { origin, direction, null, maxDistance, LayerIndex.world.mask.value };
        if (!(bool)raycastMethod.Invoke(null, args) || args[2] == null)
        {
            return double.PositiveInfinity;
        }

        return Convert.ToSingle(raycastDistanceProperty.GetValue(args[2]));
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
