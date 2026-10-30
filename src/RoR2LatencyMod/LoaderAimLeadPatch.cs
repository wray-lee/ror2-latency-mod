// 票06 LoaderAimLead：Loader 钩爪瞄准提前量（纯客户端，只改本地发射方向）。
//
// 反编译核对（F:/ror2-decomp/RoR2src，运行期 DLL 反射复核签名/访问级别）：
// - EntityStates.Loader/FireHook.cs:34-55 OnEnter：:37 `if (base.isAuthority)`；:39 GetAimRay()；
//   :40 TrajectoryAimAssist.ApplyTrajectoryAimAssist(ref ray, projectilePrefab, base.gameObject)
//   （4 参重载，radiusMultiplier 默认 1）；:41-54 position=ray.origin、rotation=LookRotation(ray.direction)
//   → 在 :40 之后改 ray.direction 即改本地发射方向，不动伤害与服务器命中。
// - RoR2/TrajectoryAimAssist.cs:99-113 该重载：弹速取 ProjectileSimple.desiredForwardSpeed(:107)、
//   maxDistance = 弹速 * aim_trajectory_assist_distance_scale(:111)；:142 SphereCastAll(hitMask :24)；
//   :159-160 方向 = normalized(targetPoint - origin)，且 dot > aim_trajectory_assist_max_change_threshold(:32)；
//   :167-217 ProcessPotentialTargets 取最近的通过 FriendlyFireManager.ShouldDirectHitProceed(:209) 的 HurtBox。
//   → 旧实现把 max-change 阈值（dot 0.8 ≈ 37°）当成候选锥，并且瞄准 HurtBox 中心；真实房间里这会把
//   钩爪拉向准星外很远的另一个敌人（“偏离很多”的头号嫌疑）。现在只沿原版射线 10°、且 HurtBox 中心
//   距射线 ≤1m 的走廊里取最近目标，找不到就不补；偏移点严格落在原版射线上，只沿目标速度切向移动。
// - 目标速度：本版本 CharacterBody 无 velocity 成员（反射确认）；CharacterMotor.cs:126 `public Vector3 velocity`，
//   但 CharacterMotor.cs:472-477 PreMove 在 !hasEffectiveAuthority 时直接 return（远端敌人/玩家即如此），
//   故远端 motor.velocity 恒为 0。改用 CharacterNetworkTransform 的服务器快照
//   （CharacterNetworkTransform.cs:107 `List<Snapshot> snapshots`、:10-24 serverTime/position、:221-246 追加/裁剪）：
//   取最后三条快照做两次差分，要求速度/方向一致、跨度足够、速度过阈值，再按置信度缩放；否则不补。
// - 提前量时间取 U + 该目标实际的 CharacterNetworkTransform.interpolationDelay（:127，0.1×2=0.2s 左右）
//   + 弹丸飞行时间，上限 AimLeadMaxSeconds（默认 0.15s 目标行程），垂直分量限制为水平速度的 35%。
//   目标静止、速度不稳、strength=0 或 cap=0 时提前量为 0，射线保持原版值。

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RoR2;
using RoR2.Networking;
using RoR2.Projectile;
using UnityEngine;

namespace RoR2LatencyMod;

public sealed class LoaderAimLeadPatch : SkillPatch
{
    private const string LoaderBodyName = "LoaderBody";
    private const string DiagKey = "LoaderAimLead.fire";

    /// <summary>Above this the snapshot delta is a teleport/respawn, not movement (m/s).</summary>
    private const float MaxTargetSpeed = 100f;

    /// <summary>Tight candidate cone: the vanilla ray already points at a locked target.</summary>
    private const float TargetProbeMaxAngleDegrees = 10f;

    /// <summary>Hurtbox centre farther than this from the vanilla ray is not treated as the target.</summary>
    private const float TargetProbeRadiusMeters = 1.0f;

    private static LoaderAimLeadPatch instance;
    private static FieldInfo snapshotsField;
    private static MethodInfo shouldUseAimAssistMethod;
    private static BodyIndex loaderBodyIndex = BodyIndex.None;
    private static bool loaderBodyIndexResolved;

    public LoaderAimLeadPatch(
        Func<bool> isEnabled,
        Func<float> aimLeadStrength,
        Func<float> aimLeadMaxSeconds,
        DiagLog diag = null,
        LatencyEstimator latency = null)
        : base("LoaderAimLead", PatchSide.Client, isEnabled, diag, latency)
    {
        AimLeadStrength = aimLeadStrength;
        AimLeadMaxSeconds = aimLeadMaxSeconds;
    }

    public Func<float> AimLeadStrength { get; }

    /// <summary>Lead displacement cap in seconds of target travel; 0 disables the lead.</summary>
    public Func<float> AimLeadMaxSeconds { get; }

    protected override void Install()
    {
        // 只挂 FireHook 实际调用的重载（FireHook.cs:40 的 IL 已核对为 4 参版本）。
        var applyAimAssist = AccessTools.Method(
            typeof(TrajectoryAimAssist),
            "ApplyTrajectoryAimAssist",
            new[] { typeof(Ray).MakeByRefType(), typeof(GameObject), typeof(GameObject), typeof(float) });
        snapshotsField = AccessTools.Field(typeof(CharacterNetworkTransform), "snapshots");
        // Vanilla's own gate (owner is a player and the user's aim-assist radius is > 0).
        // Without it, our lead would still snap aim when the player disabled trajectory aim assist.
        shouldUseAimAssistMethod = AccessTools.Method(
            typeof(TrajectoryAimAssist), "ShouldUseTrajectoryAimAssist", new[] { typeof(GameObject) });

        if (applyAimAssist == null || snapshotsField == null || shouldUseAimAssistMethod == null)
        {
            throw new MissingMemberException(
                "LoaderAimLead members missing: " +
                $"ApplyTrajectoryAimAssist={(applyAimAssist != null)}, " +
                $"CharacterNetworkTransform.snapshots={(snapshotsField != null)}, " +
                $"ShouldUseTrajectoryAimAssist={(shouldUseAimAssistMethod != null)}");
        }

        instance = this;
        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            applyAimAssist,
            postfix: new HarmonyMethod(typeof(LoaderAimLeadPatch), nameof(ApplyTrajectoryAimAssistPostfix)));

        Diag?.Info("LoaderAimLead: installed (aim lead after vanilla TrajectoryAimAssist).");
    }

    // 原版修正之后：ray 已指向原版选中的目标（或无目标时的原始瞄向），在此叠加提前量。
    private static void ApplyTrajectoryAimAssistPostfix(
        ref Ray ray,
        GameObject projectilePrefab,
        GameObject owner,
        float radiusMultiplier)
    {
        var patch = instance;
        if (patch == null || !patch.ShouldRun())
        {
            return;
        }

        var adjusted = ray;
        patch.Guard(() => adjusted = patch.ApplyLead(adjusted, projectilePrefab, owner, radiusMultiplier));
        ray = adjusted;
    }

    private Ray ApplyLead(Ray ray, GameObject projectilePrefab, GameObject owner, float radiusMultiplier)
    {
        var ownerBody = owner ? owner.GetComponent<CharacterBody>() : null;
        if (!ownerBody || !IsLoaderBody(ownerBody))
        {
            return ray; // 同一重载也被其他角色技能调用，只处理 Loader。
        }

        float strength = AimLeadStrength != null ? AimLeadStrength() : 0f;
        if (strength <= 0f)
        {
            Diag?.Throttled(DiagKey, "LoaderAimLead: AimLeadStrength=0, vanilla aim.");
            return ray;
        }

        float capSeconds = AimLeadMaxSeconds != null ? AimLeadMaxSeconds() : 0f;
        if (capSeconds <= 0f)
        {
            Diag?.Throttled(DiagKey, "LoaderAimLead: AimLeadMaxSeconds=0, vanilla aim.");
            return ray;
        }

        if (Latency == null || !Latency.TryGetOneWayLatency(out double oneWaySeconds))
        {
            return ray;
        }

        if (!(bool)shouldUseAimAssistMethod.Invoke(null, new object[] { owner }))
        {
            Diag?.Throttled(DiagKey, "LoaderAimLead: vanilla trajectory aim assist is off for this owner, vanilla aim.");
            return ray;
        }

        float projectileSpeed = 0f;
        if (projectilePrefab)
        {
            var simple = projectilePrefab.GetComponent<ProjectileSimple>();
            if (simple)
            {
                projectileSpeed = simple.desiredForwardSpeed;
            }
        }

        if (projectileSpeed < TrajectoryAimAssist.aimTrajectoryAssistMinProjectileSpeed.value)
        {
            Diag?.Throttled(
                DiagKey,
                $"LoaderAimLead: projectile speed={projectileSpeed:F0} below vanilla minimum, vanilla aim.");
            return ray;
        }

        // 不再用 37° 的 maxChange 阈值做候选锥：那只限制最终偏转，不是候选范围。
        // 只沿原版修正后的射线找“正好在准星上”的目标，找不到就不补（宁可不补也不要补歪）。
        float maxDistance = projectileSpeed * TrajectoryAimAssist.aimTrajectoryAssistProjectileDistanceScale.value;
        if (radiusMultiplier <= 0f ||
            !TryFindTargetOnRay(ray, owner, maxDistance, out var targetBody, out float targetDistance))
        {
            Diag?.Throttled(
                DiagKey,
                $"LoaderAimLead: no target within {TargetProbeRadiusMeters:F1}m of ray, vanilla aim " +
                $"(U={oneWaySeconds * 1000.0:F0}ms, strength={strength:F2}).");
            return ray;
        }

        if (!targetBody || targetDistance < 0.1f)
        {
            return ray;
        }

        if (!TryEstimateTargetVelocity(targetBody, out var estimate, out string reason))
        {
            Diag?.Throttled(
                DiagKey,
                $"LoaderAimLead: target={targetBody.name} velocity not trusted ({reason}), vanilla aim.");
            return ray;
        }

        double speed = estimate.Speed;
        if (speed > MaxTargetSpeed)
        {
            Diag?.Throttled(
                DiagKey,
                $"LoaderAimLead: target={targetBody.name} speed={speed:F0}m/s looks like a teleport, vanilla aim.");
            return ray;
        }

        Vector3 velocity = estimate.Velocity;
        double horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
        double clampedVertical = PredictionMath.ClampVerticalSpeed(
            velocity.y, horizontalSpeed, PredictionMath.AimLeadVerticalFraction);
        var leadVelocity = new Vector3(velocity.x, (float)clampedVertical, velocity.z);

        var networkTransform = targetBody.GetComponent<CharacterNetworkTransform>();
        double interpolationSeconds = networkTransform
            ? networkTransform.interpolationDelay
            : PredictionMath.DefaultInterpolationSeconds;
        double flightSeconds = targetDistance / projectileSpeed;
        double rawLeadMeters = PredictionMath.AimLead(
                                     speed, oneWaySeconds, interpolationSeconds, flightSeconds)
                                 * Math.Max(0.0, strength)
                                 * estimate.Confidence;
        double leadMeters = PredictionMath.AimLeadCapped(
            speed,
            oneWaySeconds,
            interpolationSeconds,
            flightSeconds,
            strength,
            estimate.Confidence,
            capSeconds);
        bool capped = rawLeadMeters > speed * capSeconds;

        // 目标点严格落在原版射线上，只沿目标速度切向偏移；不再瞄准 HurtBox 中心。
        Vector3 targetPoint = ray.origin + ray.direction * targetDistance;
        Vector3 vanillaDirection = ray.direction;
        Vector3 rawAimDirection = ownerBody.inputBank
            ? ownerBody.inputBank.GetAimRay().direction
            : vanillaDirection;
        Vector3 finalDirection = vanillaDirection;
        bool guardPassed = true;
        if (leadMeters > 0.0 && leadVelocity.sqrMagnitude > 1e-6f)
        {
            Vector3 leadPoint = targetPoint + leadVelocity.normalized * (float)leadMeters;
            Vector3 candidate = leadPoint - ray.origin;
            if (candidate.sqrMagnitude > 1e-6f)
            {
                candidate.Normalize();
                float maxChangeThreshold = Mathf.Clamp(
                    TrajectoryAimAssist.aimTrajectoryAssistMaxChangeThreshold.value, -1f, 1f);
                guardPassed = rawAimDirection.sqrMagnitude < 1e-6f ||
                              Vector3.Dot(rawAimDirection, candidate) >= maxChangeThreshold;
                if (guardPassed)
                {
                    finalDirection = candidate;
                    ray.direction = candidate;
                }
            }
        }

        Diag?.Throttled(
            DiagKey,
            $"LoaderAimLead: target={targetBody.name} dist={targetDistance:F1}m " +
            $"speed={speed:F2}/{estimate.PreviousSpeed:F2}m/s dot={estimate.DirectionDot:F2} " +
            $"span={estimate.SpanSeconds * 1000.0:F0}ms conf={estimate.Confidence:F2} " +
            $"vert={clampedVertical:F2} (raw {velocity.y:F2}, horiz {horizontalSpeed:F2}) " +
            $"U={oneWaySeconds * 1000.0:F0}ms interp={interpolationSeconds * 1000.0:F0}ms " +
            $"flight={flightSeconds * 1000.0:F0}ms lead={leadMeters:F2}m raw={rawLeadMeters:F2}m " +
            $"cap={capSeconds:F2}s capped={(capped ? "yes" : "no")} strength={strength:F2} " +
            $"vanillaAdj={Vector3.Angle(rawAimDirection, vanillaDirection):F2}deg " +
            $"vanillaToFinal={Vector3.Angle(vanillaDirection, finalDirection):F2}deg " +
            $"rawToFinal={Vector3.Angle(rawAimDirection, finalDirection):F2}deg " +
            $"guard={(guardPassed ? "ok" : "rejected")}");
        return ray;
    }

    /// <summary>
    /// Recovers the body the vanilla-adjusted ray points at: nearest hurtbox whose centre is within
    /// a tight cone and 1 m of the ray. The old code searched the full 37-degree max-change cone,
    /// which could snap the hook to an unrelated enemy behind/beside the crosshair.
    /// </summary>
    private static bool TryFindTargetOnRay(
        Ray ray,
        GameObject owner,
        float maxDistance,
        out CharacterBody targetBody,
        out float targetDistance)
    {
        targetBody = null;
        targetDistance = 0f;
        if (maxDistance <= 0f)
        {
            return false;
        }

        var ownerTeam = owner.GetComponent<TeamComponent>();
        TeamIndex attackerTeamIndex = ownerTeam ? ownerTeam.teamIndex : TeamIndex.None;
        var search = new BullseyeSearch
        {
            searchOrigin = ray.origin,
            searchDirection = ray.direction,
            maxDistanceFilter = maxDistance,
            maxAngleFilter = TargetProbeMaxAngleDegrees,
            sortMode = BullseyeSearch.SortMode.Angle,
            teamMaskFilter = TeamMask.all,
            filterByLoS = false,
        };
        search.RefreshCandidates();

        float nearestAlong = float.MaxValue;
        foreach (HurtBox hurtBox in search.GetResults())
        {
            if (!hurtBox)
            {
                continue;
            }

            HealthComponent health = hurtBox.healthComponent;
            if (!health || !health.alive || !health.body || health.body.gameObject == owner)
            {
                continue;
            }

            if (!FriendlyFireManager.ShouldDirectHitProceed(health, attackerTeamIndex))
            {
                continue;
            }

            Vector3 toTarget = hurtBox.transform.position - ray.origin;
            float along = Vector3.Dot(toTarget, ray.direction);
            if (along <= 0f || along > maxDistance)
            {
                continue;
            }

            float perpendicular = (toTarget - ray.direction * along).magnitude;
            if (perpendicular > TargetProbeRadiusMeters)
            {
                continue;
            }

            if (along < nearestAlong)
            {
                nearestAlong = along;
                targetBody = health.body;
            }
        }

        if (targetBody == null)
        {
            return false;
        }

        // The vanilla ray may just point at a wall (no vanilla target); never lead through it.
        // If the shared raycast helper is unavailable, stay conservative and skip the lead.
        bool wallBlocked;
        try
        {
            wallBlocked = !double.IsPositiveInfinity(
                EnforcerStartLeadPatch.RaycastDistance(ray.origin, ray.direction, nearestAlong));
        }
        catch
        {
            wallBlocked = true;
        }

        if (wallBlocked)
        {
            targetBody = null;
            return false;
        }

        targetDistance = nearestAlong;
        return true;
    }

    private struct VelocityEstimate
    {
        public Vector3 Velocity;
        public double Speed;
        public double PreviousSpeed;
        public double DirectionDot;
        public double SpanSeconds;
        public double Confidence;
    }

    /// <summary>
    /// Three CharacterNetworkTransform snapshots -> two velocity estimates. Needs the two estimates
    /// to agree in direction and magnitude, spans at least several network ticks, and a clear
    /// speed; otherwise the lead is zero.
    /// </summary>
    private static bool TryEstimateTargetVelocity(
        CharacterBody body,
        out VelocityEstimate estimate,
        out string reason)
    {
        estimate = default;
        var networkTransform = body.GetComponent<CharacterNetworkTransform>();
        if (!networkTransform || snapshotsField == null)
        {
            reason = "no CharacterNetworkTransform";
            return false;
        }

        var snapshots = snapshotsField.GetValue(networkTransform)
            as List<CharacterNetworkTransform.Snapshot>;
        if (snapshots == null || snapshots.Count < 3)
        {
            reason = "fewer than 3 snapshots";
            return false;
        }

        int last = snapshots.Count - 1;
        var oldest = snapshots[last - 2];
        var middle = snapshots[last - 1];
        var newest = snapshots[last];
        float oldDelta = middle.serverTime - oldest.serverTime;
        float newDelta = newest.serverTime - middle.serverTime;
        if (oldDelta <= 0.001f || newDelta <= 0.001f || oldDelta > 0.5f || newDelta > 0.5f)
        {
            reason = "invalid snapshot interval";
            return false;
        }

        Vector3 oldVelocity = (middle.position - oldest.position) / oldDelta;
        Vector3 newVelocity = (newest.position - middle.position) / newDelta;
        double speed = newVelocity.magnitude;
        double previousSpeed = oldVelocity.magnitude;
        double directionDot = speed > 1e-4 && previousSpeed > 1e-4
            ? Vector3.Dot(oldVelocity / (float)previousSpeed, newVelocity / (float)speed)
            : 0.0;
        double span = oldDelta + newDelta;
        double confidence = PredictionMath.AimLeadConfidence(
            speed, previousSpeed, directionDot, span);
        if (confidence <= 0.0)
        {
            reason = $"unstable speed={speed:F2}/{previousSpeed:F2} dot={directionDot:F2} " +
                     $"span={span * 1000.0:F0}ms";
            return false;
        }

        estimate = new VelocityEstimate
        {
            Velocity = newVelocity,
            Speed = speed,
            PreviousSpeed = previousSpeed,
            DirectionDot = directionDot,
            SpanSeconds = span,
            Confidence = confidence,
        };
        reason = null;
        return true;
    }

    private static bool IsLoaderBody(CharacterBody body)
    {
        if (!loaderBodyIndexResolved)
        {
            loaderBodyIndex = BodyCatalog.FindBodyIndex(LoaderBodyName);
            loaderBodyIndexResolved = loaderBodyIndex != BodyIndex.None;
        }

        return loaderBodyIndexResolved && body.bodyIndex == loaderBodyIndex;
    }
}
