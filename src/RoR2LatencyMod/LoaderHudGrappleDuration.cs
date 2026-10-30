// HUD 1：钩爪剩余时间（配置在 PatchConfig.HudGrappleDuration，默认关）。
//
// 数据链路（只读反编译核对，F:/ror2-decomp/RoR2src）：
// - ProjectileGrappleController.cs:461-465 Start 调 AssignHookReferenceToBodyStateMachine
//   （:439-460），在 owner 的 Hook 状态机处于 FireHook 时执行
//   FireHook.SetHookReference(base.gameObject)（:443）；
//   EntityStates.Loader/FireHook.cs:18/61-67 因此持有 public hookInstance（角色当前钩爪）。
// - RoR2.Projectile/ProjectileSimple.cs:11 lifetime（秒）、:36 私有 stopwatch、:95 每
//   FixedUpdate 累加、:96-99 到 lifetime 后服务器销毁钩爪。剩余秒数 = lifetime - stopwatch。
//   stopwatch 无公开读取入口，只能反射（与 LoaderFlyTimeoutPatch 反射 FlyState 字段同法）。
// - FlyState（ProjectileGrappleController.cs:119-127）的 duration = maxTravelDistance /
//   velocity 是“飞满即返”的上限，见 :129-161；Return/Overshoots 见 :342-359。本 HUD 与
//   LoaderRuler 行为一致，显示钩爪寿命倒计时。
// - 颜色：LoaderHudMath.RemainingTimeColor 在剩余比例 < 1/3 时把配置色渐变为纯红。

using System.Collections.Generic;
using System.Globalization;
using EntityStates.Loader;
using HarmonyLib;
using RoR2;
using RoR2.Projectile;
using UnityEngine;

namespace RoR2LatencyMod;

/// <summary>Pure (Unity-free) math for the grapple-duration HUD; unit-tested directly.</summary>
public static class LoaderHudGrappleDurationMath
{
    /// <summary>Same turn point as LoaderHudMath.RemainingTimeColor: red fades in below 1/3 left.</summary>
    public const double DangerFraction = 1.0 / 3.0;

    public static double RemainingFraction(double lifetimeSeconds, double elapsedSeconds)
    {
        if (!(lifetimeSeconds > 0.0) || double.IsInfinity(lifetimeSeconds))
        {
            return 0.0;
        }

        return LoaderHudMath.RemainingSeconds(lifetimeSeconds, elapsedSeconds) / lifetimeSeconds;
    }

    public static bool IsDanger(double remainingFraction)
    {
        return remainingFraction >= 0.0 && remainingFraction < DangerFraction;
    }

    public static string FormatSeconds(double seconds)
    {
        double safe = double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0
            ? 0.0
            : seconds;
        return safe.ToString("0.00", CultureInfo.InvariantCulture) + "s";
    }
}

public sealed class LoaderHudGrappleDuration : LoaderHudElement
{
    private static string Label => LabelTable.Get("hud.label.grappleDuration", LabelLanguageProbe.Current);
    private const float RefreshSeconds = 0.05f;

    // ProjectileSimple.stopwatch 是 private（ProjectileSimple.cs:36），没有公开的已飞时间。
    // FieldRef 直接读字段，避免每帧 FieldInfo.GetValue 装箱。
    private static readonly AccessTools.FieldRef<ProjectileSimple, float> ReadStopwatch =
        AccessTools.FieldRefAccess<ProjectileSimple, float>("stopwatch");

    private ProjectileGrappleController hook;
    private ProjectileSimple hookSimple;
    private string cachedValue;
    private Color cachedColor;
    private bool cachedDanger;
    private float refreshCountdown;

    protected override LoaderHudReadout Read(in LoaderHudContext context)
    {
        ProjectileGrappleController controller = LoaderHudGrappleProbe.Find(context.Body, ref hook);
        if (!controller)
        {
            hookSimple = null;
            ResetReadoutCache();
            return LoaderHudReadout.None;
        }

        if (!hookSimple || hookSimple.gameObject != controller.gameObject)
        {
            hookSimple = controller.GetComponent<ProjectileSimple>();
            ResetReadoutCache();
        }

        if (!hookSimple)
        {
            ResetReadoutCache();
            return LoaderHudReadout.None;
        }

        refreshCountdown -= context.DeltaSeconds;
        if (cachedValue != null && refreshCountdown > 0f)
        {
            return new LoaderHudReadout(Label, cachedValue, cachedColor, cachedDanger);
        }

        float elapsed = ReadStopwatch(hookSimple);
        double fraction = LoaderHudGrappleDurationMath.RemainingFraction(hookSimple.lifetime, elapsed);
        cachedDanger = LoaderHudGrappleDurationMath.IsDanger(fraction);
        if (cachedDanger)
        {
            Color configured = Config.TextColor.Value;
            RgbColor rgb = LoaderHudMath.RemainingTimeColor(
                new RgbColor(configured.r, configured.g, configured.b), fraction);
            float alpha = Mathf.Clamp01(configured.a * Config.Opacity.Value);
            cachedColor = new Color((float)rgb.R, (float)rgb.G, (float)rgb.B, alpha);
        }
        else
        {
            cachedColor = default;
        }

        double remaining = LoaderHudMath.RemainingSeconds(hookSimple.lifetime, elapsed);
        cachedValue = LoaderHudGrappleDurationMath.FormatSeconds(remaining);
        refreshCountdown = RefreshSeconds;
        return new LoaderHudReadout(Label, cachedValue, cachedColor, cachedDanger);
    }

    private void ResetReadoutCache()
    {
        cachedValue = null;
        cachedColor = default;
        cachedDanger = false;
        refreshCountdown = 0f;
    }
}

/// <summary>
/// Resolves the ProjectileGrappleController owned by a body (local or spectated).
/// Prefers the FireHook.SetHookReference instance; if replication ordering left it unset,
/// falls back to an owner match only while the body's Hook state machine is firing.
/// </summary>
internal static class LoaderHudGrappleProbe
{
    private const string HookStateMachineName = "Hook";

    internal static ProjectileGrappleController Find(
        CharacterBody body, ref ProjectileGrappleController cached)
    {
        if (!body)
        {
            cached = null;
            return null;
        }

        if (!(FindHookStateMachine(body)?.state is FireHook fireHook))
        {
            cached = null;
            return null;
        }

        // 当前 hookInstance 优先：旧钩仍存活时，缓存不能压过刚发射的新钩。
        GameObject hook = fireHook.hookInstance;
        if (hook)
        {
            ProjectileGrappleController current = hook.GetComponent<ProjectileGrappleController>();
            if (current)
            {
                cached = current;
                return cached;
            }
        }

        if (IsOwnedBy(cached, body))
        {
            return cached;
        }

        cached = null;

        // Spectators can miss SetHookReference when the projectile spawns before the FireHook
        // state replicates (the original bug LoaderFlyTimeoutPatch covers). The game's projectile
        // registry avoids FindObjectsOfType allocations; only the firing state reaches this path.
        List<ProjectileController> projectiles = InstanceTracker.GetInstancesList<ProjectileController>();
        for (int i = 0; i < projectiles.Count; i++)
        {
            ProjectileController projectile = projectiles[i];
            if (!projectile || projectile.owner != body.gameObject)
            {
                continue;
            }

            ProjectileGrappleController controller = projectile.GetComponent<ProjectileGrappleController>();
            if (controller)
            {
                cached = controller;
                return cached;
            }
        }

        return null;
    }

    private static CharacterBody cachedMachineBody;
    private static EntityStateMachine cachedHookMachine;
    private static float nextMachineSearchTime;

    private static EntityStateMachine FindHookStateMachine(CharacterBody body)
    {
        if (body == cachedMachineBody)
        {
            if (cachedHookMachine)
            {
                return cachedHookMachine;
            }

            if (Time.time < nextMachineSearchTime)
            {
                return null;
            }
        }

        cachedMachineBody = body;
        cachedHookMachine = EntityStateMachine.FindByCustomName(body.gameObject, HookStateMachineName);
        nextMachineSearchTime = Time.time + 1f;
        return cachedHookMachine;
    }

    private static bool IsOwnedBy(ProjectileGrappleController controller, CharacterBody body)
    {
        if (!controller || !body)
        {
            return false;
        }

        ProjectileController projectile = controller.GetComponent<ProjectileController>();
        return projectile && projectile.owner == body.gameObject;
    }
}
