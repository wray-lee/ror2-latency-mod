using System;
using System.Globalization;
using EntityStates.Loader;
using RoR2;
using RoR2.Projectile;
using UnityEngine;

namespace RoR2LatencyMod;

/// <summary>
/// HUD 6: grapple energy estimate (experimental, config PatchConfig.HudGrappleEnergy,
/// off by default; the RoO entry is labelled 实验). Shows while the hook is stuck:
/// kinetic + gravitational + rope-tension + slam potential via LoaderHudMath.GrappleEnergy.
/// </summary>
public sealed class LoaderHudGrappleEnergy : LoaderHudElement
{
    private const float RefreshSeconds = 0.1f;

    private ProjectileGrappleController cachedGrapple;
    private GameObject cachedStickObject;
    private ProjectileStickOnImpact stick;
    private LoaderHudMotionSample motion;

    private string cachedValue = "0 J";
    private bool valueValid;
    private float refreshCountdown;

    protected override void OnHidden()
    {
        motion = default;
        valueValid = false;
        refreshCountdown = 0f;
    }

    protected override LoaderHudReadout Read(in LoaderHudContext context)
    {
        CharacterBody body = context.Body;
        if (body == null || body.characterMotor is null)
        {
            valueValid = false;
            return LoaderHudReadout.None;
        }

        ProjectileGrappleController grapple = LoaderHudGrappleProbe.Find(body, ref cachedGrapple);
        if (grapple == null)
        {
            valueValid = false;
            return LoaderHudReadout.None;
        }

        if (cachedStickObject != grapple.gameObject)
        {
            cachedStickObject = grapple.gameObject;
            stick = grapple.GetComponent<ProjectileStickOnImpact>();
            motion = default;
            valueValid = false;
            refreshCountdown = 0f;
        }

        // 钩住期间：ProjectileStickOnImpact.stuck 在命中世界或 hurtbox 后为真。
        if (stick == null || !stick.stuck)
        {
            motion = default;
            valueValid = false;
            return LoaderHudReadout.None;
        }

        // Velocity sampling stays per-frame (cheap, keeps the elapsed-time accounting exact);
        // energy, ground raycast and string formatting are limited to 10 Hz.
        if (!LoaderHudMotion.TryGetVelocity(body, ref motion, context.DeltaSeconds, out Vector3 velocity))
        {
            valueValid = false;
            return LoaderHudReadout.None;
        }

        refreshCountdown -= context.DeltaSeconds;
        if (valueValid && refreshCountdown > 0f)
        {
            return new LoaderHudReadout(
                LabelTable.Get("hud.label.grappleEnergy", LabelLanguageProbe.Current), cachedValue);
        }

        double ropeLength = RopeLength(body, grapple);
        if (double.IsNaN(ropeLength))
        {
            valueValid = false;
            return LoaderHudReadout.None;
        }

        var motor = body.characterMotor;
        double gravity = Math.Abs(Physics.gravity.y * motor.gravityScale * motor.gravityMultiplier);
        double slamAcceleration = SlamAcceleration(gravity);
        double energy = LoaderGrappleEnergyMath.Estimate(
            motor.mass,
            velocity.magnitude,
            GroundHeight(body),
            ropeLength,
            gravity,
            slamAcceleration);

        cachedValue = LoaderGrappleEnergyMath.FormatJoules(energy);
        refreshCountdown = RefreshSeconds;
        valueValid = true;
        return new LoaderHudReadout(
            LabelTable.Get("hud.label.grappleEnergy", LabelLanguageProbe.Current), cachedValue);
    }

    // Vertical distance to world geometry; no hit (bottomless pit) stays invalid and the
    // pure estimator substitutes MinimumSafeHeightMeters.
    private static double GroundHeight(CharacterBody body)
    {
        Vector3 origin = body.corePosition + Vector3.up * 0.1f;
        if (Physics.Raycast(
                origin,
                Vector3.down,
                out RaycastHit hit,
                10000f,
                LayerIndex.world.mask,
                QueryTriggerInteraction.Ignore))
        {
            return Math.Max(0f, origin.y - hit.point.y);
        }

        return double.NaN;
    }

    // Same hook-end position/distance as ProjectileGrappleController.cs:66-69 and :188-190;
    // reuse the pure grapple-length helper from the grapple-length HUD.
    private static double RopeLength(CharacterBody body, ProjectileGrappleController controller)
    {
        Transform hookTransform = controller.transform;
        Vector3 aimOrigin = body.aimOrigin;
        Vector3 hookPosition = hookTransform.position;
        Vector3 hookUp = hookTransform.up;
        return LoaderHudGrappleLengthMath.HookDistance(
            new Vec3(aimOrigin.x, aimOrigin.y, aimOrigin.z),
            new Vec3(hookPosition.x, hookPosition.y, hookPosition.z),
            new Vec3(hookUp.x, hookUp.y, hookUp.z),
            controller.normalOffset);
    }

    // GroundSlam applies verticalAcceleration (GroundSlam.cs:26,69). The static field is
    // populated by the state configuration; fall back to gravity if it is not set yet.
    private static double SlamAcceleration(double gravity)
    {
        double slam = Math.Abs(GroundSlam.verticalAcceleration);
        return IsFinite(slam) && slam > 0.0 ? slam : gravity;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

/// <summary>Pure render-side rules for the experimental grapple energy HUD.</summary>
public static class LoaderGrappleEnergyMath
{
    /// <summary>Display cap, keeps absurd/overflow values from producing huge energy strings.</summary>
    public const double MaximumDisplayJoules = 1000000000.0;

    /// <summary>Clamp height at the render boundary (the pure estimator also clamps).</summary>
    public static double SafeHeight(double heightMeters)
    {
        return double.IsNaN(heightMeters) || double.IsInfinity(heightMeters) ||
               heightMeters < LoaderHudMath.MinimumSafeHeightMeters
            ? LoaderHudMath.MinimumSafeHeightMeters
            : heightMeters;
    }

    /// <summary>0.5*m*v^2 + m*g*h + m*g*rope + m*slam*h with safe height, then display clamp.</summary>
    public static double Estimate(
        double mass, double speed, double heightMeters, double ropeLengthMeters,
        double gravityAcceleration, double slamAcceleration)
    {
        return ClampJoules(LoaderHudMath.GrappleEnergy(
            mass,
            speed,
            SafeHeight(heightMeters),
            ropeLengthMeters,
            gravityAcceleration,
            slamAcceleration));
    }

    public static double ClampJoules(double joules)
    {
        if (double.IsNaN(joules) || joules <= 0.0)
        {
            return 0.0;
        }

        return joules >= MaximumDisplayJoules ? MaximumDisplayJoules : joules;
    }

    public static string FormatJoules(double joules)
    {
        double value = ClampJoules(joules);
        if (value >= 1000000.0)
        {
            return (value / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + " MJ";
        }

        if (value >= 1000.0)
        {
            return (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " kJ";
        }

        return value.ToString("0", CultureInfo.InvariantCulture) + " J";
    }
}
