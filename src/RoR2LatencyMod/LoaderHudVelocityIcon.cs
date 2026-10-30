using System;
using RoR2;
using UnityEngine;
using UnityEngine.UI;

namespace RoR2LatencyMod;

/// <summary>
/// HUD 5: velocity direction icon (config PatchConfig.HudVelocityIcon, off by default).
/// Arrow follows the velocity direction projected onto the HUD plane, fades in with speed
/// and tints toward green as the aim direction approaches the velocity direction.
/// </summary>
public sealed class LoaderHudVelocityIcon : LoaderHudElement
{
    /// <summary>
    /// Game-built-in path form used by LoaderRuler ("Textures/MiscIcons/texBarrelIcon");
    /// we use the game's arrow icon instead. LegacyResourcesAPI resolves this legacy path
    /// through the game's Addressables GUID map and caches the asset below.
    /// </summary>
    public const string DefaultIconAddress = "Textures/MiscIcons/texBasicArrowRight";

    /// <summary>Icon sprite path; empty forces the built-in UI arrow. Read once, then cached.</summary>
    public static string IconAddress = DefaultIconAddress;

    /// <summary>True: config color blended toward green by aim alignment. False: static config color.</summary>
    public static bool UseAimGradient = true;

    private const float IconSizePixels = 32f;
    private const float ArrowShaftLength = 24f;
    private const float ArrowShaftWidth = 3f;
    private const float ArrowHeadLength = 10f;
    private const float ArrowHeadOffset = 4f;

    private static Sprite cachedIcon;
    private static string cachedIconAddress;
    private static bool iconLoadAttempted;

    private RectTransform iconRoot;
    private Image iconImage;
    private Image arrowShaft;
    private Image arrowHeadUpper;
    private Image arrowHeadLower;

    private LoaderHudMotionSample motion;
    private int shownFrame = -1;
    private float appliedAngle = float.NaN;
    private Color appliedTint;
    private bool tintApplied;
    private Vector2 appliedAnchor = new Vector2(float.NaN, float.NaN);
    private Vector2 appliedParentSize;
    private Vector2 appliedOffset;
    private float appliedScale = float.NaN;

    private void Awake()
    {
        // 首次可见时才同步加载会卡一帧；初始化先取一次，结果静态缓存（同理 S10）。
        EnsureIconLoaded();
    }

    protected override LoaderHudReadout Read(in LoaderHudContext context)
    {
        CharacterBody body = context.Body;
        if (body == null)
        {
            return LoaderHudReadout.None;
        }

        if (!LoaderHudMotion.TryGetVelocity(body, ref motion, context.DeltaSeconds, out Vector3 velocity))
        {
            return LoaderHudReadout.None;
        }

        double speed = velocity.magnitude;
        double visibility = LoaderVelocityIconMath.Visibility(speed);
        if (visibility <= 0.0)
        {
            return LoaderHudReadout.None;
        }

        // uiCam is deliberately fixed at identity (CameraRigController.cs:355-359);
        // direction must come from the gameplay camera that is viewing the target.
        Camera camera = context.Hud != null && context.Hud.cameraRigController != null
            ? context.Hud.cameraRigController.sceneCam
            : null;
        if (camera == null)
        {
            camera = Camera.main;
        }

        if (camera == null)
        {
            return LoaderHudReadout.None;
        }

        // Velocity pointing straight into/out of the screen has no HUD-plane direction.
        Vector3 cameraRelative = camera.transform.InverseTransformDirection(velocity);
        if (cameraRelative.x * cameraRelative.x + cameraRelative.y * cameraRelative.y < 0.0001f)
        {
            return LoaderHudReadout.None;
        }

        // 观战远端 body 的 aimDirection 无网络复制，回退 transform.forward，颜色只是近似（S9）。
        Vector3 aim = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
        if (aim.sqrMagnitude < 0.000001f)
        {
            aim = body.transform.forward;
        }

        double dot = LoaderHudMath.DirectionDot(
            new Vec3(velocity.x, velocity.y, velocity.z),
            new Vec3(aim.x, aim.y, aim.z));

        EnsureVisual();
        UpdatePlacement();

        float angle = Mathf.Atan2(cameraRelative.y, cameraRelative.x) * Mathf.Rad2Deg;
        if (!iconRoot.gameObject.activeSelf)
        {
            iconRoot.gameObject.SetActive(true);
        }

        if (float.IsNaN(appliedAngle) || Mathf.Abs(angle - appliedAngle) > 0.01f)
        {
            appliedAngle = angle;
            iconRoot.localEulerAngles = new Vector3(0f, 0f, angle);
        }

        Color configured = Config.TextColor.Value;
        float alpha = Mathf.Clamp01(configured.a * Config.Opacity.Value) * (float)visibility;
        Color tint;
        if (UseAimGradient)
        {
            RgbColor aligned = LoaderVelocityIconMath.AimColor(dot);
            tint = new Color((float)aligned.R, (float)aligned.G, (float)aligned.B, alpha);
        }
        else
        {
            tint = new Color(configured.r, configured.g, configured.b, alpha);
        }

        ApplyTint(tint);
        shownFrame = Time.frameCount;
        return LoaderHudReadout.None;
    }

    protected override void OnHidden()
    {
        motion = default;
    }

    // Base.Tick only calls Read while enabled/visible; hide the icon on every other frame.
    private void LateUpdate()
    {
        if (iconRoot == null)
        {
            return;
        }

        try
        {
            if (Config == null || Faulted || !Config.Enabled.Value || shownFrame != Time.frameCount)
            {
                if (iconRoot.gameObject.activeSelf)
                {
                    iconRoot.gameObject.SetActive(false);
                }
            }
        }
        catch (Exception ex)
        {
            Fault(ex, null);
        }
    }

    private void EnsureVisual()
    {
        if (iconRoot != null)
        {
            return;
        }

        var rect = (RectTransform)transform;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(IconSizePixels, IconSizePixels);

        var iconObject = new GameObject("VelocityIcon", typeof(RectTransform));
        iconRoot = (RectTransform)iconObject.transform;
        iconRoot.SetParent(transform, false);
        iconRoot.anchorMin = new Vector2(0.5f, 0.5f);
        iconRoot.anchorMax = new Vector2(0.5f, 0.5f);
        iconRoot.pivot = new Vector2(0.5f, 0.5f);
        iconRoot.sizeDelta = new Vector2(IconSizePixels, IconSizePixels);
        iconRoot.anchoredPosition = Vector2.zero;

        iconImage = iconObject.AddComponent<Image>();
        iconImage.raycastTarget = false;
        iconImage.preserveAspect = true;
        iconImage.enabled = false;

        arrowShaft = CreateArrowPart(
            "Shaft", new Vector2(ArrowShaftLength, ArrowShaftWidth),
            new Vector2(-2f, 0f), 0f);
        arrowHeadUpper = CreateArrowPart(
            "HeadUpper", new Vector2(ArrowHeadLength, ArrowShaftWidth),
            new Vector2(12f, ArrowHeadOffset), 45f);
        arrowHeadLower = CreateArrowPart(
            "HeadLower", new Vector2(ArrowHeadLength, ArrowShaftWidth),
            new Vector2(12f, -ArrowHeadOffset), -45f);

        EnsureIconLoaded();
        bool hasIcon = cachedIcon != null;
        if (hasIcon)
        {
            iconImage.sprite = cachedIcon;
        }

        iconImage.enabled = hasIcon;
        arrowShaft.enabled = !hasIcon;
        arrowHeadUpper.enabled = !hasIcon;
        arrowHeadLower.enabled = !hasIcon;
    }

    private Image CreateArrowPart(string name, Vector2 size, Vector2 position, float zDegrees)
    {
        var partObject = new GameObject(name, typeof(RectTransform));
        var partRect = (RectTransform)partObject.transform;
        partRect.SetParent(iconRoot, false);
        partRect.anchorMin = new Vector2(0.5f, 0.5f);
        partRect.anchorMax = new Vector2(0.5f, 0.5f);
        partRect.pivot = new Vector2(0.5f, 0.5f);
        partRect.sizeDelta = size;
        partRect.anchoredPosition = position;
        partRect.localEulerAngles = new Vector3(0f, 0f, zDegrees);

        var image = partObject.AddComponent<Image>();
        image.raycastTarget = false;
        return image;
    }

    private static void EnsureIconLoaded()
    {
        string address = IconAddress;
        if (iconLoadAttempted && string.Equals(address, cachedIconAddress, StringComparison.Ordinal))
        {
            return;
        }

        iconLoadAttempted = true;
        cachedIconAddress = address;
        cachedIcon = null;
        if (string.IsNullOrEmpty(address))
        {
            return;
        }

        try
        {
            // LegacyResourcesAPI lives in its own assembly which this project does not
            // reference (csproj is out of this task's file scope). DifficultyDef.GetIconSprite
            // is the game's own public wrapper around LegacyResourcesAPI.Load<Sprite>, so a
            // throwaway DifficultyDef lets us resolve the same legacy/Addressables path.
            var probe = new DifficultyDef(
                1f, "LoaderHudVelocityIcon", address, "LoaderHudVelocityIcon", Color.white, string.Empty, false);
            cachedIcon = probe.GetIconSprite();
        }
        catch (Exception)
        {
            // Icon is optional: a bad/missing path falls back to the built-in UI arrow.
            // Allow one retry at first visible frame in case assets were not ready during Awake.
            cachedIcon = null;
            iconLoadAttempted = false;
            cachedIconAddress = null;
        }
    }

    // Match LoaderHudText placement: normalized anchor + parent-size-relative offset (uiScale=1
    // because the HUD parent rect is already canvas-local), plus the configured scale.
    private void UpdatePlacement()
    {
        var parent = transform.parent as RectTransform;
        if (parent == null)
        {
            return;
        }

        var anchor = new Vector2(Config.AnchorX.Value, Config.AnchorY.Value);
        Vector2 parentSize = parent.rect.size;
        Vec2 pixelOffset = LoaderHudMath.AnchorPixelOffset(
            Config.OffsetX.Value, Config.OffsetY.Value, parentSize.x, parentSize.y, 1.0);
        var offset = new Vector2((float)pixelOffset.X, (float)pixelOffset.Y);
        float scale = Config.Scale.Value;
        if (anchor == appliedAnchor && parentSize == appliedParentSize &&
            offset == appliedOffset && scale == appliedScale)
        {
            return;
        }

        appliedAnchor = anchor;
        appliedParentSize = parentSize;
        appliedOffset = offset;
        appliedScale = scale;

        var rect = (RectTransform)transform;
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.anchoredPosition = offset;
        rect.localScale = new Vector3(scale, scale, 1f);
    }

    private void ApplyTint(Color tint)
    {
        if (tintApplied && tint == appliedTint)
        {
            return;
        }

        tintApplied = true;
        appliedTint = tint;
        iconImage.color = tint;
        arrowShaft.color = tint;
        arrowHeadUpper.color = tint;
        arrowHeadLower.color = tint;
    }
}

/// <summary>Pure rules for the velocity icon: speed fade-in and aim-alignment tint.</summary>
public static class LoaderVelocityIconMath
{
    /// <summary>Speed at which the icon is fully opaque.</summary>
    public const double FullVisibilitySpeedMetersPerSecond = 20.0;

    // Smoothstep 3t^2-2t^3 on speed/20, clamped; invalid/NaN speed is invisible.
    public static double Visibility(double speedMetersPerSecond)
    {
        if (double.IsNaN(speedMetersPerSecond) || speedMetersPerSecond <= 0.0)
        {
            return 0.0;
        }

        if (speedMetersPerSecond >= FullVisibilitySpeedMetersPerSecond)
        {
            return 1.0;
        }

        double t = speedMetersPerSecond / FullVisibilitySpeedMetersPerSecond;
        return t * t * (3.0 - 2.0 * t);
    }

    // dot (cosine of the angle between aim and velocity) -> red / yellow / green.
    public static RgbColor AimColor(double directionDot)
    {
        double dot = double.IsNaN(directionDot) ? 0.0 : Math.Max(-1.0, Math.Min(1.0, directionDot));
        return LoaderHudMath.AimAlignmentColor(0.5 * (dot + 1.0));
    }
}

internal struct LoaderHudMotionSample
{
    public CharacterBody Body;
    public IPhysMotor Motor;
    public Vector3 PreviousPosition;
    public Vector3 Velocity;
    public float ElapsedSinceSample;
    public float ElapsedSinceChange;
    public bool HasPreviousPosition;
}

/// <summary>
/// Velocity for local and spectated Loaders, matching LoaderHudSpeedText.MeasureSpeed:
/// an authoritative body reads IPhysMotor.velocity. A spectated body's transform is updated on
/// fixed ticks, so speed = position delta / time since the last position change (actual elapsed,
/// never an assumed fixed step); unchanged frames hold the last value, and a stop decays after 0.1s.
/// </summary>
internal static class LoaderHudMotion
{
    private const float HoldSeconds = 0.1f;

    public static bool TryGetVelocity(
        CharacterBody body, ref LoaderHudMotionSample sample, float deltaSeconds, out Vector3 velocity)
    {
        velocity = Vector3.zero;
        if (body == null)
        {
            return false;
        }

        if (sample.Body != body)
        {
            sample = new LoaderHudMotionSample
            {
                Body = body,
                Motor = body.GetComponent<IPhysMotor>(),
            };
        }

        if (body.hasAuthority && sample.Motor != null)
        {
            sample.HasPreviousPosition = false;
            sample.Velocity = sample.Motor.velocity;
            velocity = sample.Velocity;
            return IsFinite(velocity);
        }

        Vector3 position = body.transform.position;
        if (!sample.HasPreviousPosition)
        {
            sample.PreviousPosition = position;
            sample.HasPreviousPosition = true;
            sample.ElapsedSinceSample = 0f;
            sample.ElapsedSinceChange = 0f;
            sample.Velocity = Vector3.zero;
            return false;
        }

        if (deltaSeconds > 0f)
        {
            sample.ElapsedSinceSample += deltaSeconds;
            sample.ElapsedSinceChange += deltaSeconds;
        }

        if (position == sample.PreviousPosition)
        {
            if (sample.ElapsedSinceChange > HoldSeconds)
            {
                // Stopped: discard the idle time so a later move is not divided by it.
                sample.Velocity = Vector3.zero;
                sample.ElapsedSinceSample = 0f;
            }

            velocity = sample.Velocity;
            return IsFinite(velocity);
        }

        Vector3 delta = position - sample.PreviousPosition;
        sample.PreviousPosition = position;
        double speed = LoaderHudSpeedTextMath.PositionDeltaSpeed(
            delta.magnitude, sample.ElapsedSinceSample);
        sample.Velocity = speed > 0.0 && delta.sqrMagnitude > 0f
            ? delta * (float)(speed / delta.magnitude)
            : Vector3.zero;

        sample.ElapsedSinceSample = 0f;
        sample.ElapsedSinceChange = 0f;
        velocity = sample.Velocity;
        return IsFinite(velocity);
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) &&
               !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
    }
}
