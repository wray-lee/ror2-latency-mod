using System;
using System.Collections.Generic;

namespace RoR2LatencyMod;

public readonly struct Vec2
{
    public readonly double X;
    public readonly double Y;

    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct Vec3
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public readonly struct RgbColor
{
    public readonly double R;
    public readonly double G;
    public readonly double B;

    public RgbColor(double r, double g, double b)
    {
        R = LoaderHudMath.Clamp01(r);
        G = LoaderHudMath.Clamp01(g);
        B = LoaderHudMath.Clamp01(b);
    }
}

public readonly struct PunchHit
{
    public readonly int PunchId;
    public readonly double HitSeconds;
    public readonly double SlamSeconds;
    public readonly double Speed;
    public readonly double AimDot;

    public PunchHit(int punchId, double hitSeconds, double slamSeconds, double speed, double aimDot)
    {
        PunchId = punchId;
        HitSeconds = hitSeconds;
        SlamSeconds = slamSeconds;
        Speed = speed;
        AimDot = aimDot;
    }
}

public readonly struct PunchRecord
{
    public readonly double Speed;
    public readonly double AimDot;
    public readonly bool SlammedFirst;

    public PunchRecord(double speed, double aimDot, bool slammedFirst)
    {
        Speed = speed;
        AimDot = aimDot;
        SlammedFirst = slammedFirst;
    }
}

/// <summary>
/// Pure HUD math for the Loader module. No Unity or game types.
/// </summary>
public static class LoaderHudMath
{
    /// <summary>Speed at which the speed text reaches its configured maximum size.</summary>
    public const double SpeedForMaximumFontMetersPerSecond = 100.0;

    /// <summary>Height used when the ground probe fails or reports less than this (void protection).</summary>
    public const double MinimumSafeHeightMeters = 0.5;

    // Raised-cosine ramp: smooth start, no overshoot, hard cap at maxSize.
    public static double SpeedFontSize(double speedMetersPerSecond, double baseSize, double maxSize)
    {
        double min = NonNegativeFinite(baseSize);
        double max = NonNegativeFinite(maxSize);
        if (max < min)
        {
            max = min;
        }

        double t;
        if (double.IsNaN(speedMetersPerSecond) || speedMetersPerSecond <= 0.0)
        {
            t = 0.0;
        }
        else if (speedMetersPerSecond >= SpeedForMaximumFontMetersPerSecond)
        {
            t = 1.0;
        }
        else
        {
            t = speedMetersPerSecond / SpeedForMaximumFontMetersPerSecond;
        }

        double curve = 0.5 * (1.0 - Math.Cos(Math.PI * t));
        return min + (max - min) * curve;
    }

    // Frame-rate independent exponential approach: current -> target at rate per second.
    public static double DampToward(double current, double target, double ratePerSecond, double deltaSeconds)
    {
        if (!IsFinite(current))
        {
            return IsFinite(target) ? target : 0.0;
        }
        if (!IsFinite(target))
        {
            return current;
        }
        if (!(ratePerSecond > 0.0) || !(deltaSeconds > 0.0))
        {
            return current;
        }

        double blend = 1.0 - Math.Exp(-ratePerSecond * deltaSeconds);
        return current + (target - current) * blend;
    }

    public static RgbColor LerpColor(RgbColor from, RgbColor to, double t)
    {
        double k = Clamp01(t);
        return new RgbColor(
            from.R + (to.R - from.R) * k,
            from.G + (to.G - from.G) * k,
            from.B + (to.B - from.B) * k);
    }

    // Above one third left the configured color is kept; below it the color fades to pure red,
    // reaching red at zero. Continuous at the 1/3 turn point.
    public static RgbColor RemainingTimeColor(RgbColor configured, double remainingFraction)
    {
        double remaining = Clamp01(remainingFraction);
        double fade = remaining >= 1.0 / 3.0
            ? 0.0
            : (1.0 / 3.0 - remaining) / (1.0 / 3.0);
        return LerpColor(configured, new RgbColor(1.0, 0.0, 0.0), fade);
    }

    // Aiming away is red, half aligned is yellow, fully aligned with velocity is green.
    public static RgbColor AimAlignmentColor(double alignmentFraction)
    {
        double alignment = Clamp01(alignmentFraction);
        return new RgbColor(1.0 - alignment, alignment, 0.0);
    }

    public static double DirectionDot(Vec3 velocity, Vec3 aimDirection)
    {
        double vx = velocity.X;
        double vy = velocity.Y;
        double vz = velocity.Z;
        double ax = aimDirection.X;
        double ay = aimDirection.Y;
        double az = aimDirection.Z;
        if (!IsFinite(vx) || !IsFinite(vy) || !IsFinite(vz) ||
            !IsFinite(ax) || !IsFinite(ay) || !IsFinite(az))
        {
            return 0.0;
        }

        double vLengthSquared = vx * vx + vy * vy + vz * vz;
        double aLengthSquared = ax * ax + ay * ay + az * az;
        if (!(vLengthSquared > 0.0) || !(aLengthSquared > 0.0))
        {
            return 0.0;
        }

        double dot = (vx * ax + vy * ay + vz * az) / Math.Sqrt(vLengthSquared * aLengthSquared);
        if (double.IsNaN(dot))
        {
            return 0.0;
        }
        return dot > 1.0 ? 1.0 : dot < -1.0 ? -1.0 : dot;
    }

    public static double AngleDegrees(double dot)
    {
        double clamped = double.IsNaN(dot) ? 0.0 : dot > 1.0 ? 1.0 : dot < -1.0 ? -1.0 : dot;
        return Math.Acos(clamped) * (180.0 / Math.PI);
    }

    public static double AlignmentPercent(double dot)
    {
        return Clamp01(dot) * 100.0;
    }

    /// <summary>
    /// Kinetic + gravity + rope-tension + slam potential, in joules:
    /// 0.5*m*v^2 + m*g*h + m*g*rope + m*slam*h.
    /// Gravity and slam are accelerations; their sign is ignored.
    /// </summary>
    public static double GrappleEnergy(
        double mass, double speed, double height, double ropeLength,
        double gravity, double slamAcceleration)
    {
        mass = NonNegativeFinite(mass);
        speed = NonNegativeFinite(speed);
        ropeLength = NonNegativeFinite(ropeLength);
        gravity = NonNegativeFinite(Math.Abs(gravity));
        slamAcceleration = NonNegativeFinite(Math.Abs(slamAcceleration));

        // No ground below (void) or an unusably small drop: use the safe fallback.
        double safeHeight = !IsFinite(height) || height < MinimumSafeHeightMeters
            ? MinimumSafeHeightMeters
            : height;

        double kinetic = 0.5 * mass * speed * speed;
        double gravityPotential = mass * gravity * safeHeight;
        double ropeTensionPotential = mass * gravity * ropeLength;
        double slamPotential = mass * slamAcceleration * safeHeight;
        return kinetic + gravityPotential + ropeTensionPotential + slamPotential;
    }

    public static double RemainingSeconds(double lifetimeSeconds, double elapsedSeconds)
    {
        if (!IsFinite(lifetimeSeconds) || lifetimeSeconds <= 0.0)
        {
            return 0.0;
        }

        double elapsed = double.IsNaN(elapsedSeconds) ? 0.0
            : elapsedSeconds < 0.0 ? 0.0
            : elapsedSeconds;
        double remaining = lifetimeSeconds - elapsed;
        return double.IsNaN(remaining) || remaining <= 0.0 ? 0.0 : remaining;
    }

    public static bool IsThunderslamFirst(double slamSeconds, double hitSeconds)
    {
        return IsFinite(slamSeconds) && IsFinite(hitSeconds) &&
            slamSeconds >= 0.0 && slamSeconds <= hitSeconds;
    }

    /// <summary>
    /// One record per punch (the first target hit), newest hit first, capped at maxCount.
    /// Equal hit times keep input order. SlammedFirst means the Thunderslam landed first.
    /// </summary>
    public static PunchRecord[] BuildPunchRecords(IReadOnlyList<PunchHit> hits, int maxCount)
    {
        if (hits == null || maxCount <= 0)
        {
            return Array.Empty<PunchRecord>();
        }

        var firstIndexByPunch = new Dictionary<int, int>();
        var candidates = new List<PunchHit>();
        for (int i = 0; i < hits.Count; i++)
        {
            PunchHit hit = hits[i];
            if (!IsFinite(hit.HitSeconds) || !IsFinite(hit.Speed))
            {
                continue;
            }

            PunchHit cleaned = new PunchHit(
                hit.PunchId,
                hit.HitSeconds,
                hit.SlamSeconds,
                NonNegativeFinite(hit.Speed),
                Clamp01(hit.AimDot));

            if (firstIndexByPunch.TryGetValue(cleaned.PunchId, out int existingIndex))
            {
                if (cleaned.HitSeconds < candidates[existingIndex].HitSeconds)
                {
                    candidates[existingIndex] = cleaned;
                }
                continue;
            }

            firstIndexByPunch.Add(cleaned.PunchId, candidates.Count);
            candidates.Add(cleaned);
        }

        var order = new List<int>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            order.Add(i);
        }

        // Newest first; indices break ties so equal hit times keep input order.
        order.Sort((a, b) =>
        {
            int byTime = candidates[b].HitSeconds.CompareTo(candidates[a].HitSeconds);
            return byTime != 0 ? byTime : a.CompareTo(b);
        });

        int count = Math.Min(maxCount, order.Count);
        var records = new PunchRecord[count];
        for (int i = 0; i < count; i++)
        {
            PunchHit hit = candidates[order[i]];
            records[i] = new PunchRecord(
                hit.Speed,
                hit.AimDot,
                IsThunderslamFirst(hit.SlamSeconds, hit.HitSeconds));
        }
        return records;
    }

    /// <summary>
    /// Fractional anchor offset -> canvas pixels. Doubles with the configured UI scale.
    /// </summary>
    public static Vec2 AnchorPixelOffset(
        double relativeX, double relativeY, double canvasWidth, double canvasHeight, double uiScale)
    {
        if (!IsFinite(relativeX) || !IsFinite(relativeY) ||
            !IsFinite(canvasWidth) || !IsFinite(canvasHeight) || !IsFinite(uiScale))
        {
            return new Vec2(0.0, 0.0);
        }
        if (canvasWidth <= 0.0 || canvasHeight <= 0.0 || uiScale <= 0.0)
        {
            return new Vec2(0.0, 0.0);
        }

        return new Vec2(
            relativeX * canvasWidth * uiScale,
            relativeY * canvasHeight * uiScale);
    }

    internal static double Clamp01(double value)
    {
        if (double.IsNaN(value) || value <= 0.0)
        {
            return 0.0;
        }
        return value >= 1.0 ? 1.0 : value;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double NonNegativeFinite(double value)
    {
        if (!IsFinite(value) || value < 0.0)
        {
            return 0.0;
        }
        return value;
    }
}
