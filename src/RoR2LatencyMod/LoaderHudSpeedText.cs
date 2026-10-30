// Loader HUD 3：速度文字（配置在 PatchConfig.HudSpeedText，默认关）。
//
// 语义（只读反编译 F:/ror2-decomp/RoR2src/EntityStates.Loader 核对）：
// - BaseChargeFist.cs:115 OnEnter 进入蓄力；ChargeFist/ChargeZapFist 派生自它。
// - BaseSwingChargedFist.cs:47-59 OnEnter 把 characterMotor.velocity 设为 punchVelocity，
//   并在 :57 写 punchSpeed。
// - BaseSwingChargedFist.cs:113-117 CalculateLungeVelocity 沿瞄准方向叠加冲刺速度。
// 所以蓄力/挥拳期间显示“速度方向 · 瞄准方向”点积；越小说明瞄偏、速度损失越多。
// 本地 body 用 CharacterMotor.velocity；观战 body 的 transform 只在固定帧更新，因此用
// 位置差 / 距上次位置变化的时间（实际计时），不能按渲染帧间隔或固定步长硬除。

using System;
using System.Globalization;
using EntityStates.Loader;
using RoR2;
using UnityEngine;

namespace RoR2LatencyMod;

/// <summary>速度文字的纯映射/格式化；无 Unity 类型，可单测。</summary>
public static class LoaderHudSpeedTextMath
{
    /// <summary>字号最多放大到配置字号的多少倍（硬上限，避免极端速度溢出）。</summary>
    public const double MaxFontScale = 1.6;

    public static double FontScale(double speedMetersPerSecond, double baseFontSize, double maxFontSize)
    {
        if (!(baseFontSize > 0.0) || double.IsNaN(baseFontSize) || double.IsInfinity(baseFontSize))
        {
            return 1.0;
        }

        double max = maxFontSize > baseFontSize ? maxFontSize : baseFontSize;
        return LoaderHudMath.SpeedFontSize(speedMetersPerSecond, baseFontSize, max) / baseFontSize;
    }

    /// <summary>Position delta over the actual elapsed frame time (never the fixed timestep).</summary>
    public static double PositionDeltaSpeed(double distanceMeters, double frameDeltaSeconds)
    {
        if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || !(distanceMeters > 0.0) ||
            double.IsNaN(frameDeltaSeconds) || double.IsInfinity(frameDeltaSeconds) || !(frameDeltaSeconds > 0.0))
        {
            return 0.0;
        }

        return distanceMeters / frameDeltaSeconds;
    }

    public static string FormatSpeed(double speedMetersPerSecond)
    {
        double speed = double.IsNaN(speedMetersPerSecond) || double.IsInfinity(speedMetersPerSecond) ||
            speedMetersPerSecond < 0.0
            ? 0.0
            : speedMetersPerSecond;
        return speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s";
    }

    public static string FormatAimDot(double aimDot)
    {
        double dot = double.IsNaN(aimDot) ? 0.0 : aimDot > 1.0 ? 1.0 : aimDot < -1.0 ? -1.0 : aimDot;
        return dot.ToString("0.00", CultureInfo.InvariantCulture);
    }
}

public sealed class LoaderHudSpeedText : LoaderHudElement
{
    /// <summary>可选覆盖：返回 null 时用内置测量。</summary>
    public static Func<LoaderHudContext, double?> SpeedProvider;

    /// <summary>可选覆盖：蓄力拳期间返回“速度方向 · 瞄准方向”点积；返回 null 时用内置状态判断。</summary>
    public static Func<LoaderHudContext, double?> AimDotProvider;

    private CharacterBody cachedBody;
    private EntityStateMachine[] stateMachines;
    private LoaderHudMotionSample motion;
    private Vector3 lastVelocity;

    private int cachedSpeedKey = int.MinValue;
    private int cachedAimKey = int.MinValue;
    private bool cachedAimVisible;
    private string cachedSpeedText;
    private string cachedAimText;
    private string cachedValue;

    protected override LoaderHudReadout Read(in LoaderHudContext context)
    {
        CharacterBody body = context.Body;
        if (body == null)
        {
            return LoaderHudReadout.None;
        }

        EnsureBody(body);
        double speed = SanitizeSpeed(MeasureSpeed(body, context.DeltaSeconds));
        double? overrideSpeed = SpeedProvider != null ? SpeedProvider(context) : null;
        if (overrideSpeed.HasValue && !double.IsNaN(overrideSpeed.Value))
        {
            speed = SanitizeSpeed(overrideSpeed.Value);
        }

        bool aimVisible;
        double aimDot;
        double? overrideAim = AimDotProvider != null ? AimDotProvider(context) : null;
        if (overrideAim.HasValue)
        {
            aimVisible = true;
            aimDot = overrideAim.Value;
        }
        else
        {
            aimVisible = IsChargedFistState();
            aimDot = aimVisible ? MeasureAimDot(body) : 0.0;
        }

        int speedKey = (int)Math.Round(speed * 10.0, MidpointRounding.AwayFromZero);
        int aimKey = (int)Math.Round(ClampAimDot(aimDot) * 100.0, MidpointRounding.AwayFromZero);
        bool changed = false;

        if (speedKey != cachedSpeedKey)
        {
            cachedSpeedKey = speedKey;
            cachedSpeedText = LoaderHudSpeedTextMath.FormatSpeed(speedKey / 10.0);
            changed = true;
        }

        if (aimVisible)
        {
            if (!cachedAimVisible || aimKey != cachedAimKey)
            {
                cachedAimKey = aimKey;
                cachedAimText = "<size=65%>" +
                    LabelTable.Get("hud.label.aim", LabelLanguageProbe.Current) + " " +
                    LoaderHudSpeedTextMath.FormatAimDot(aimKey / 100.0) + "</size>";
                changed = true;
            }
        }
        else if (cachedAimVisible)
        {
            changed = true;
        }

        cachedAimVisible = aimVisible;
        if (changed || cachedValue == null)
        {
            cachedValue = aimVisible ? cachedSpeedText + "\n" + cachedAimText : cachedSpeedText;
        }

        double baseSize = Config.FontSize.Value;
        double scale = LoaderHudSpeedTextMath.FontScale(
            speed, baseSize, baseSize * LoaderHudSpeedTextMath.MaxFontScale);
        return new LoaderHudReadout(
            LabelTable.Get("hud.label.speed", LabelLanguageProbe.Current),
            cachedValue, default, false, (float)scale);
    }

    private void EnsureBody(CharacterBody body)
    {
        if (body == cachedBody)
        {
            return;
        }

        cachedBody = body;
        // Loader 可能挂着多个状态机（例如 Hook）；GetComponent 只拿第一个，顺序不保证。
        stateMachines = body.GetComponents<EntityStateMachine>();
        motion = default;
        cachedSpeedKey = int.MinValue;
        cachedAimKey = int.MinValue;
        cachedAimVisible = false;
        cachedSpeedText = null;
        cachedAimText = null;
        cachedValue = null;
    }

    private double MeasureSpeed(CharacterBody body, float deltaSeconds)
    {
        if (!LoaderHudMotion.TryGetVelocity(
                body, ref motion, deltaSeconds, out Vector3 velocity))
        {
            lastVelocity = Vector3.zero;
            return 0.0;
        }

        lastVelocity = velocity;
        return velocity.magnitude;
    }

    protected override void OnHidden()
    {
        motion = default;
    }

    private bool IsChargedFistState()
    {
        if (stateMachines == null)
        {
            return false;
        }

        for (int i = 0; i < stateMachines.Length; i++)
        {
            EntityStateMachine machine = stateMachines[i];
            if (machine == null)
            {
                continue;
            }

            object state = machine.state;
            if (state is BaseChargeFist || state is BaseSwingChargedFist)
            {
                return true;
            }
        }

        return false;
    }

    private double MeasureAimDot(CharacterBody body)
    {
        // 观战远端 body 的 aimDirection 无网络复制，InputBankTest 会回退 transform.forward；
        // 此时这里只是近似值（无新增网络消息的客户端方案）。见 S9。
        Vector3 aim = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
        return LoaderHudMath.DirectionDot(
            new Vec3(lastVelocity.x, lastVelocity.y, lastVelocity.z),
            new Vec3(aim.x, aim.y, aim.z));
    }

    private static double ClampAimDot(double dot)
    {
        if (double.IsNaN(dot))
        {
            return 0.0;
        }

        return dot > 1.0 ? 1.0 : dot < -1.0 ? -1.0 : dot;
    }

    private static double SanitizeSpeed(double speed)
    {
        if (double.IsNaN(speed) || speed < 0.0)
        {
            return 0.0;
        }

        return double.IsPositiveInfinity(speed) || speed > 1000000.0 ? 1000000.0 : speed;
    }
}
