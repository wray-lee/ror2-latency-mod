// 票07 LoaderCrosshair：钩爪准心的射程 / 蓄力拳 / 冲刺三处客户端修正。
//
// 反编译核对（F:/ror2-decomp/RoR2src，只读）：
// - RoR2.UI/LoaderHookCrosshairController.cs:49-64：Update 在 :57 用 secondary.CanExecute() 置
//   available，在 :61 用 inputBank.GetAimRaycast(range) 做单点射线；range 是 HUD prefab 的序列化值。
//   InputBankTest.cs:164-186 会把原点换到相机并加 extraRaycastDistance，但仍是单点射线。本版本
//   LoaderCrosshair.prefab 的 range=80，与 LoaderHook.prefab 的 ProjectileGrappleController
//   .maxTravelDistance=80 相同，但钩爪是半径 0.25 的 SphereCollider，单点射线会漏掉贴边目标，
//   因此这里按可用射程显示：maxTravelDistance × 超时倍率 + 碰撞半径。超时倍率含固定 50ms
//   stuck 同步余量（默认 80 × 2.225 + 0.25 = 178.25m），但不含随网络抖动的 U，避免准星跳变；
//   LoaderRuler 的 Fix Grapple Range 显式关闭时退回 base+ownExtra（默认 134.25m）。
//   准星只在本地玩家身上改 range；观战他人 Loader、补丁未安装/熔断/关闭时保留 prefab 原值。
//   详见 docs/loader-hud-spec.md。
// - RoR2.UI/CrosshairManager.cs:80-117：未锁定时每帧选 prefab；:91 在
//   targetBody.isSprinting && useSprintCrosshair 时直接选 sprintCrosshairPrefabOverride 或
//   _sprintingCrosshair（SprintingCrosshair），完全绕过 CrosshairUtils.GetCrosshairPrefabForBody
//   （CrosshairUtils.cs:164-176）；:98 才触发 pickCrosshairGlobal，:109 实例化。这里只在
//   UpdateCrosshair 调用期间把 override 换成 Loader 的普通准心，调用结束即还原（含异常路径），
//   不改任何持久状态。
// - EntityStates.Loader/BaseChargeFist.cs:87,152-154：蓄力到 minChargeForChargedAttack 后请求
//   crosshairOverridePrefab（OnExit :135 释放）；资产里该引用是 StraightBracketCrosshair，它没有
//   LoaderHookCrosshairController，也没有 HookIndicatorInRange 子节点，所以蓄力时射程指示消失。
// - EntityStates.Loader/BaseSwingChargedFist.cs:80：挥拳期间把 characterBody.isSprinting 置真，
//   于是还会被 SprintingCrosshair 覆盖（CrosshairManager.cs:91）。
// 实现：给蓄力准心补一个 LoaderHookCrosshairController（游戏自带组件）并从 Loader 默认准心
// 克隆 HookIndicatorInRange 子节点，自己接线显示/隐藏；不写 IL、不改 prefab 资产。
// 仅客户端 HUD：不新增网络消息，不改伤害/命中/技能状态。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using EntityStates.Loader;
using HarmonyLib;
using RoR2;
using RoR2.Projectile;
using RoR2.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace RoR2LatencyMod;

public sealed class LoaderCrosshairPatch : SkillPatch
{
    private const string Tag = "LoaderCrosshair";
    private const string LoaderBodyName = "LoaderBody";
    private const string HookIndicatorChildName = "HookIndicatorInRange";
    private const string ChargeIndicatorName = "LoaderHookRangeIndicator";
    private const string LoaderRulerGuid = "dolso.loaderruler";
    private const string LoaderRulerSection = "Fixes";
    private const string LoaderRulerKey = "Fix Grapple Range";

    private static LoaderCrosshairPatch instance;
    private static bool loaderBodyIndexResolved;
    private static BodyIndex loaderBodyIndex;

    // Prefab data is stable per run; the range itself is recomputed so config/Ruler toggles apply.
    private static HookRangeData[] hookRangeData;
    private static float nextHookRangeScanTime;
    private static bool hookRangeScanFailureLogged;
    private static bool hookRangeModelLogged;

    private static bool rulerProbed;
    private static bool rulerPresent;
    private static bool rulerFixEnabled;
    private static float nextRulerProbeTime;
    private static bool rulerProbeFailureLogged;

    private struct HookRangeData
    {
        public float MaxTravelDistance;
        public float ForwardSpeed;
        public float ColliderRadius;
    }

    /// <summary>Prefix state: the original sprint override, put back by the finalizer.</summary>
    private sealed class SprintOverrideState
    {
        public GameObject Previous;
    }

    /// <summary>Prefab range captured before the first write, restored when the patch stops.</summary>
    private sealed class RangeBackup
    {
        public bool Captured;
        public float Value;
    }

    private static readonly ConditionalWeakTable<LoaderHookCrosshairController, RangeBackup>
        originalRanges = new ConditionalWeakTable<LoaderHookCrosshairController, RangeBackup>();

    public LoaderCrosshairPatch(Func<bool> isEnabled, DiagLog diag = null)
        : base("LoaderCrosshair", PatchSide.Client, isEnabled, diag)
    {
    }

    // Crosshair fixes are local HUD only; they are useful in single-player and on the host too.
    protected override bool RequiresRemoteClient => false;

    protected override void Install()
    {
        var hookUpdate = AccessTools.Method(typeof(LoaderHookCrosshairController), "Update");
        var updateCrosshair = AccessTools.Method(typeof(CrosshairManager), "UpdateCrosshair");
        if (hookUpdate == null || updateCrosshair == null)
        {
            throw new MissingMemberException(
                $"LoaderCrosshair members missing: Update={hookUpdate != null}, " +
                $"CrosshairManager.UpdateCrosshair={updateCrosshair != null}");
        }

        instance = this;
        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            hookUpdate,
            prefix: new HarmonyMethod(typeof(LoaderCrosshairPatch), nameof(HookCrosshairUpdatePrefix)));
        harmony.Patch(
            updateCrosshair,
            prefix: new HarmonyMethod(typeof(LoaderCrosshairPatch), nameof(UpdateCrosshairPrefix)),
            postfix: new HarmonyMethod(typeof(LoaderCrosshairPatch), nameof(UpdateCrosshairPostfix)),
            finalizer: new HarmonyMethod(typeof(LoaderCrosshairPatch), nameof(UpdateCrosshairFinalizer)));

        Diag?.Info(
            "LoaderCrosshair: installed (full hook range, hook indicator while charging fist, " +
            "no sprint-crosshair override for LoaderBody).");
    }

    /// <summary>Before vanilla's range check: use the hook projectile's real reach, not the UI value.</summary>
    private static void HookCrosshairUpdatePrefix(LoaderHookCrosshairController __instance)
    {
        var self = instance;
        if (__instance == null)
        {
            return;
        }

        if (self == null || !self.ShouldRun())
        {
            RestoreOriginalRange(__instance);
            return;
        }

        self.Guard(() =>
        {
            // Spectated Loaders keep their own reach: only the local player's crosshair may use
            // this client's config. Also undo a live-config disable/spectate switch cleanly.
            var body = __instance.GetComponent<HudElement>()?.targetCharacterBody;
            if (body == null || !body.hasAuthority)
            {
                RestoreOriginalRange(__instance);
                return;
            }

            var backup = originalRanges.GetOrCreateValue(__instance);
            if (!backup.Captured)
            {
                backup.Value = __instance.range;
                backup.Captured = true;
            }

            float range = self.FullRangeMeters();
            if (range > 0f)
            {
                __instance.range = range;
            }
        });
    }

    private static void RestoreOriginalRange(LoaderHookCrosshairController controller)
    {
        if (controller == null || !originalRanges.TryGetValue(controller, out var backup))
        {
            return;
        }

        if (backup.Captured)
        {
            controller.range = backup.Value;
        }

        originalRanges.Remove(controller);
    }

    /// <summary>Loader sprinting: substitute its normal/override crosshair for the sprint one.</summary>
    private static void UpdateCrosshairPrefix(CharacterBody targetBody, out SprintOverrideState __state)
    {
        __state = null;
        var self = instance;
        if (self == null || targetBody == null || !self.ShouldRun())
        {
            return;
        }

        bool wantsNormalCrosshair = false;
        self.Guard(() =>
            wantsNormalCrosshair =
                IsLoaderBody(targetBody) && targetBody.isSprinting && targetBody.useSprintCrosshair);
        if (!wantsNormalCrosshair)
        {
            return;
        }

        SprintOverrideState state = null;
        self.Guard(() =>
        {
            var normal = CrosshairUtils.GetCrosshairPrefabForBody(targetBody);
            if (normal == null)
            {
                return;
            }

            state = new SprintOverrideState { Previous = targetBody.sprintCrosshairPrefabOverride };
            targetBody.sprintCrosshairPrefabOverride = normal;
        });
        __state = state;
    }

    private static Exception UpdateCrosshairFinalizer(
        Exception __exception, CharacterBody targetBody, SprintOverrideState __state)
    {
        if (__state != null && targetBody != null)
        {
            try
            {
                targetBody.sprintCrosshairPrefabOverride = __state.Previous;
            }
            catch (Exception exception)
            {
                instance?.Diag?.Warning(
                    $"{Tag}: sprint crosshair override restore failed ({exception.GetType().Name}).");
            }
        }

        return __exception;
    }

    /// <summary>While the charge-fist override is active, give it the hook range indicator.</summary>
    private static void UpdateCrosshairPostfix(
        CharacterBody targetBody,
        GameObject ___currentCrosshairPrefab,
        CrosshairController ___crosshairController)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        if (!self.ShouldRun())
        {
            RemoveChargeRangeIndicator(___crosshairController);
            return;
        }

        if (targetBody == null || ___currentCrosshairPrefab == null || ___crosshairController == null)
        {
            return;
        }

        self.Guard(() => self.EnsureChargeRangeIndicator(
            targetBody, ___currentCrosshairPrefab, ___crosshairController));
    }

    private void EnsureChargeRangeIndicator(
        CharacterBody body, GameObject prefab, CrosshairController controller)
    {
        if (prefab != BaseChargeFist.crosshairOverridePrefab)
        {
            return;
        }

        if (!IsLoaderBody(body) || !body.hasAuthority)
        {
            // Spectating another Loader or a non-Loader target: drop any indicator this patch
            // added to a reused crosshair object so it cannot show local numbers for them.
            RemoveChargeRangeIndicator(controller);
            return;
        }

        var crosshairObject = controller.gameObject;
        // Only the vanilla charge crosshair (StraightBracketCrosshair) gets here; the marker
        // component distinguishes our augmented instance from the default Loader crosshair.
        if (crosshairObject.GetComponent<LoaderHookCrosshairController>() != null)
        {
            return;
        }

        var defaultPrefab = body.defaultCrosshairPrefab;
        var source = defaultPrefab != null
            ? defaultPrefab.transform.Find(HookIndicatorChildName)
            : null;
        if (source == null)
        {
            return;
        }

        var indicator = UnityEngine.Object.Instantiate(source.gameObject, crosshairObject.transform);
        indicator.name = ChargeIndicatorName;
        // The prefab leaves the Image disabled and lets its serialized UnityEvent enable it;
        // a runtime clone has no such event, so drive the Image from our listeners instead.
        var indicatorImage = indicator.GetComponent<Image>();
        if (indicatorImage == null)
        {
            UnityEngine.Object.Destroy(indicator);
            return;
        }

        indicatorImage.enabled = false;

        var hook = crosshairObject.AddComponent<LoaderHookCrosshairController>();
        // Runtime-added components get no serialized UnityEvents; SetAvailable/SetInRange would
        // otherwise throw on the first state change.
        hook.onAvailable = new UnityEvent();
        hook.onUnavailable = new UnityEvent();
        hook.onEnterRange = new UnityEvent();
        hook.onExitRange = new UnityEvent();
        hook.onEnterRange.AddListener(() =>
        {
            if (indicatorImage != null)
            {
                indicatorImage.enabled = true;
            }
        });
        hook.onExitRange.AddListener(() =>
        {
            if (indicatorImage != null)
            {
                indicatorImage.enabled = false;
            }
        });

        float range = FullRangeMeters();
        if (range > 0f)
        {
            hook.range = range;
        }

        Diag?.Info($"{Tag}: charge-fist crosshair now shows the hook range indicator.");
    }

    /// <summary>Removes only the indicator/controller this patch added; vanilla crosshairs stay.</summary>
    private static void RemoveChargeRangeIndicator(CrosshairController controller)
    {
        if (controller == null)
        {
            return;
        }

        try
        {
            var crosshairObject = controller.gameObject;
            var indicator = crosshairObject.transform.Find(ChargeIndicatorName);
            if (indicator == null)
            {
                return;
            }

            var hook = crosshairObject.GetComponent<LoaderHookCrosshairController>();
            if (hook != null)
            {
                UnityEngine.Object.Destroy(hook);
            }

            UnityEngine.Object.Destroy(indicator.gameObject);
        }
        catch (Exception exception)
        {
            // Cleanup must not break the vanilla HUD, especially while the patch is tripped.
            instance?.Diag?.Warning(
                $"{Tag}: charge indicator cleanup skipped ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// #2: usable reach = flight distance under the current timeout (including the fixed
    /// stuck-sync tick, U excluded) + hook radius. The multiplier comes from the same deadline
    /// model as LoaderFlyTimeout, so the crosshair follows LoaderRuler on/off and the patch's
    /// actual runtime state instead of the prefab's serialized 80 m.
    /// </summary>
    private float FullRangeMeters()
    {
        if (!TryGetHookRangeData(out var candidates))
        {
            return 0f;
        }

        float margin = PatchConfig.FlyTimeoutMargin?.Value ?? 0.1f;
        float extraCap = PatchConfig.FlyTimeoutExtraMaxSeconds?.Value ?? 0.15f;
        double ownExtra = Math.Min(margin, extraCap);
        // Runtime state, not the raw config: a failed install, trip, live switch-off or host
        // skip means the hook really has vanilla reach and the crosshair must say so.
        bool flyTimeoutEnabled = LoaderFlyTimeoutPatch.RuntimeActive;
        bool provideRulerEquivalent = PatchConfig.LoaderRulerEquivalent?.Value ?? true;
        bool rulerFix = ProbeLoaderRulerFix(out bool rulerWasPresent);

        // LoaderHook and LoaderYankHook can differ in speed; show the greater reach.
        HookRangeData best = default;
        double bestMultiplier = 1.0;
        double range = 0.0;
        foreach (var candidate in candidates)
        {
            double multiplier = PredictionMath.HookTimeoutMultiplier(
                candidate.MaxTravelDistance, candidate.ForwardSpeed, ownExtra,
                flyTimeoutEnabled, provideRulerEquivalent, rulerFix, rulerWasPresent,
                PredictionMath.StuckSyncTickSeconds);
            double candidateRange = PredictionMath.HookRangeMeters(
                candidate.MaxTravelDistance, multiplier, candidate.ColliderRadius);
            if (candidateRange > range)
            {
                range = candidateRange;
                best = candidate;
                bestMultiplier = multiplier;
            }
        }

        if (!hookRangeModelLogged && range > 0.0)
        {
            hookRangeModelLogged = true;
            Diag?.Info(
                $"{Tag}: reach {range:F2}m = maxTravel {best.MaxTravelDistance:F2}m x " +
                $"multiplier {bestMultiplier:F3} + radius {best.ColliderRadius:F2}m " +
                $"(speed {best.ForwardSpeed:F0} m/s, flyTimeout={flyTimeoutEnabled}, " +
                $"fixedStuckGrace={PredictionMath.StuckSyncTickSeconds:F2}s, U=excluded, " +
                $"rulerEquivalent={provideRulerEquivalent}, rulerPresent={rulerWasPresent}, " +
                $"rulerFix={rulerFix}).");
        }

        return range > 0.0 && range <= float.MaxValue ? (float)range : 0f;
    }

    /// <summary>
    /// #7: only Loader's own hooks, so a mod's longer FireHook cannot light the Loader crosshair
    /// past its real reach. Cache a successful scan; retry failures on a cooldown and log once.
    /// </summary>
    private bool TryGetHookRangeData(out HookRangeData[] data)
    {
        if (hookRangeData != null)
        {
            data = hookRangeData;
            return true;
        }

        data = null;
        float now = Time.time;
        if (now < nextHookRangeScanTime)
        {
            return false;
        }

        try
        {
            int count = ProjectileCatalog.projectilePrefabCount;
            if (count <= 0)
            {
                nextHookRangeScanTime = now + 1f; // catalog not ready yet
                return false;
            }

            var found = new List<HookRangeData>(2);
            for (int i = 0; i < count; i++)
            {
                var prefab = ProjectileCatalog.GetProjectilePrefab(i);
                if (prefab == null || !IsLoaderHookPrefabName(prefab.name))
                {
                    continue;
                }

                var grapple = prefab.GetComponent<ProjectileGrappleController>();
                if (grapple == null)
                {
                    continue;
                }

                var ownerState = grapple.ownerHookStateType.stateType;
                if (ownerState == null || !typeof(FireHook).IsAssignableFrom(ownerState))
                {
                    continue;
                }

                var simple = prefab.GetComponent<ProjectileSimple>();
                var collider = prefab.GetComponent<SphereCollider>();
                found.Add(new HookRangeData
                {
                    MaxTravelDistance = grapple.maxTravelDistance,
                    ForwardSpeed = simple != null ? simple.desiredForwardSpeed : 0f,
                    ColliderRadius = collider != null ? collider.radius : 0f,
                });
            }

            if (found.Count > 0)
            {
                hookRangeData = found.ToArray();
                data = hookRangeData;
                return true;
            }

            LogScanFailureOnce("no LoaderHook/LoaderYankHook projectile found");
            nextHookRangeScanTime = now + 5f;
            return false;
        }
        catch (Exception exception)
        {
            LogScanFailureOnce($"{exception.GetType().Name}: {exception.Message}");
            nextHookRangeScanTime = now + 5f;
            return false;
        }
    }

    private void LogScanFailureOnce(string reason)
    {
        if (hookRangeScanFailureLogged)
        {
            return;
        }

        hookRangeScanFailureLogged = true;
        Diag?.Warning(
            $"{Tag}: hook projectile scan failed ({reason}); " +
            "keeping the crosshair's serialized range.");
    }

    // shortcut: exact vanilla prefab names; a renamed/new Loader hook falls back to the prefab range.
    private static bool IsLoaderHookPrefabName(string name)
        => name == "LoaderHook" || name == "LoaderYankHook";

    /// <summary>Mirror LoaderFlyTimeout's Ruler check without touching that patch's private state.</summary>
    private static bool ProbeLoaderRulerFix(out bool present)
    {
        float now = Time.time;
        if (rulerProbed && now < nextRulerProbeTime)
        {
            present = rulerPresent;
            return rulerFixEnabled;
        }

        rulerProbed = true;
        nextRulerProbeTime = now + 0.5f;
        rulerPresent = false;
        rulerFixEnabled = false;
        try
        {
            if (Chainloader.PluginInfos.TryGetValue(LoaderRulerGuid, out var info))
            {
                rulerPresent = true;
                if (info?.Instance != null)
                {
                    var entry = info.Instance.Config?[LoaderRulerSection, LoaderRulerKey];
                    // Older Ruler builds without the key default to on.
                    rulerFixEnabled = entry == null || (entry.BoxedValue is bool value && value);
                }
            }
        }
        catch (Exception exception)
        {
            if (!rulerProbeFailureLogged)
            {
                rulerProbeFailureLogged = true;
                instance?.Diag?.Warning(
                    $"{Tag}: LoaderRuler detection failed ({exception.GetType().Name}: " +
                    $"{exception.Message}); assuming its fix is off.");
            }
        }

        present = rulerPresent;
        return rulerFixEnabled;
    }

    private static bool IsLoaderBody(CharacterBody body)
    {
        if (body == null)
        {
            return false;
        }

        if (!loaderBodyIndexResolved)
        {
            loaderBodyIndex = BodyCatalog.FindBodyIndex(LoaderBodyName);
            loaderBodyIndexResolved = loaderBodyIndex != BodyIndex.None;
        }

        return loaderBodyIndexResolved && body.bodyIndex == loaderBodyIndex;
    }
}
