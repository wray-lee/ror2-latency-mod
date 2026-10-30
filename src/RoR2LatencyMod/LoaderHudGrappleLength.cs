// HUD 2：钩爪长度（配置在 PatchConfig.HudGrappleLength，默认关）。
//
// 数据链路（只读反编译核对，F:/ror2-decomp/RoR2src）：
// - ProjectileGrappleController.cs:65-69 BaseState.UpdatePositions 每固定帧计算
//   position = 钩爪 transform.position + transform.up * normalOffset（:406 默认 1）。
// - 同文件 :189/195 BaseGripState 用 Vector3.Distance(aimOrigin, position) 做钩距判断，
//   aimOrigin = owner.characterBody.aimOrigin（:67；CharacterBody.cs:2361-2369）。
// - 钩爪实例归属见 LoaderHudGrappleProbe（LoaderHudGrappleDuration.cs）。

using System;
using System.Globalization;
using RoR2;
using RoR2.Projectile;
using UnityEngine;

namespace RoR2LatencyMod;

/// <summary>Pure (Unity-free) math for the grapple-length HUD; unit-tested directly.</summary>
public static class LoaderHudGrappleLengthMath
{
    /// <summary>
    /// Distance from the body's aim origin to the hook point, in meters; NaN when an input is
    /// not finite so the caller can skip rendering instead of showing a bogus value.
    /// </summary>
    public static double HookDistance(
        Vec3 aimOrigin, Vec3 hookPosition, Vec3 hookUp, double normalOffset)
    {
        if (!IsFinite(aimOrigin.X) || !IsFinite(aimOrigin.Y) || !IsFinite(aimOrigin.Z) ||
            !IsFinite(hookPosition.X) || !IsFinite(hookPosition.Y) || !IsFinite(hookPosition.Z) ||
            !IsFinite(hookUp.X) || !IsFinite(hookUp.Y) || !IsFinite(hookUp.Z) ||
            !IsFinite(normalOffset))
        {
            return double.NaN;
        }

        double x = hookPosition.X + hookUp.X * normalOffset;
        double y = hookPosition.Y + hookUp.Y * normalOffset;
        double z = hookPosition.Z + hookUp.Z * normalOffset;
        double dx = x - aimOrigin.X;
        double dy = y - aimOrigin.Y;
        double dz = z - aimOrigin.Z;
        double squared = dx * dx + dy * dy + dz * dz;
        return IsFinite(squared) ? Math.Sqrt(squared) : double.NaN;
    }

    public static string FormatMeters(double meters)
    {
        double safe = double.IsNaN(meters) || double.IsInfinity(meters) || meters < 0.0
            ? 0.0
            : meters;
        return safe.ToString("0.0", CultureInfo.InvariantCulture) + " m";
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

public sealed class LoaderHudGrappleLength : LoaderHudElement
{
    private static string Label => LabelTable.Get("hud.label.grappleLength", LabelLanguageProbe.Current);
    private const float RefreshSeconds = 0.1f;

    private ProjectileGrappleController hook;
    private ProjectileGrappleController cachedController;
    private string cachedValue;
    private float refreshCountdown;

    protected override LoaderHudReadout Read(in LoaderHudContext context)
    {
        ProjectileGrappleController controller = LoaderHudGrappleProbe.Find(context.Body, ref hook);
        CharacterBody body = context.Body;
        if (!controller || !body)
        {
            cachedController = null;
            cachedValue = null;
            return LoaderHudReadout.None;
        }

        if (cachedController != controller)
        {
            cachedController = controller;
            cachedValue = null;
            refreshCountdown = 0f;
        }

        refreshCountdown -= context.DeltaSeconds;
        if (cachedValue != null && refreshCountdown > 0f)
        {
            return new LoaderHudReadout(Label, cachedValue);
        }

        Transform hookTransform = controller.transform;
        Vector3 aimOrigin = body.aimOrigin;
        Vector3 hookPosition = hookTransform.position;
        Vector3 hookUp = hookTransform.up;
        double meters = LoaderHudGrappleLengthMath.HookDistance(
            new Vec3(aimOrigin.x, aimOrigin.y, aimOrigin.z),
            new Vec3(hookPosition.x, hookPosition.y, hookPosition.z),
            new Vec3(hookUp.x, hookUp.y, hookUp.z),
            controller.normalOffset);
        if (double.IsNaN(meters))
        {
            cachedValue = null;
            return LoaderHudReadout.None;
        }

        cachedValue = LoaderHudGrappleLengthMath.FormatMeters(meters);
        refreshCountdown = RefreshSeconds;
        return new LoaderHudReadout(Label, cachedValue);
    }
}
