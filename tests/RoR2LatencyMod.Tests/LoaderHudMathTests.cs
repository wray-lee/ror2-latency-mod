using System;
using Xunit;

namespace RoR2LatencyMod.Tests;

public class LoaderHudMathTests
{
    private static void AssertRgb(RgbColor actual, double r, double g, double b)
    {
        Assert.Equal(r, actual.R, 12);
        Assert.Equal(g, actual.G, 12);
        Assert.Equal(b, actual.B, 12);
    }

    private static void AssertVec2(Vec2 actual, double x, double y)
    {
        Assert.Equal(x, actual.X, 12);
        Assert.Equal(y, actual.Y, 12);
    }

    // 1) Speed -> font size. Hand-computed with a raised-cosine ramp:
    //    t = clamp(speed / 100), curve = (1 - cos(pi*t)) / 2, size = base + (max-base)*curve.
    [Theory]
    [InlineData(0.0, 12.0, 24.0, 12.0)]
    [InlineData(-10.0, 12.0, 24.0, 12.0)]
    [InlineData(25.0, 12.0, 24.0, 13.757359312880715)] // curve = (1-cos(pi/4))/2 = 0.1464466094067262
    [InlineData(50.0, 12.0, 24.0, 18.0)]                // curve = (1-cos(pi/2))/2 = 0.5
    [InlineData(100.0, 12.0, 24.0, 24.0)]
    [InlineData(250.0, 12.0, 24.0, 24.0)]               // capped
    [InlineData(double.NaN, 12.0, 24.0, 12.0)]
    [InlineData(double.PositiveInfinity, 12.0, 24.0, 24.0)]
    [InlineData(50.0, 20.0, 10.0, 20.0)]                // max below base is raised to base
    public void SpeedFontSizeRisesWithSpeedAndCapsAtMaximum(
        double speed, double baseSize, double maxSize, double expected)
    {
        Assert.Equal(expected, LoaderHudMath.SpeedFontSize(speed, baseSize, maxSize), 12);
    }

    // Exponential damping: current + (target-current) * (1 - exp(-rate*dt)).
    [Theory]
    [InlineData(10.0, 20.0, 1.0, 0.0, 10.0)]   // no time passes
    [InlineData(10.0, 20.0, 0.0, 1.0, 10.0)]   // rate 0 freezes
    [InlineData(10.0, 20.0, 1.0, 0.6931471805599453, 15.0)] // dt = ln 2 -> halfway
    [InlineData(10.0, 20.0, 10.0, 100.0, 20.0)]             // one long step converges
    [InlineData(double.NaN, 20.0, 1.0, 1.0, 20.0)]
    [InlineData(10.0, double.NaN, 1.0, 1.0, 10.0)]
    public void FontDampingIsFrameRateIndependent(
        double current, double target, double rate, double dt, double expected)
    {
        Assert.Equal(expected, LoaderHudMath.DampToward(current, target, rate, dt), 9);
    }

    [Fact]
    public void FontDampingUsesSpeedMappingAsTarget()
    {
        // target = SpeedFontSize(100) = 24; halfway between 12 and 24 is 18.
        double target = LoaderHudMath.SpeedFontSize(100.0, 12.0, 24.0);
        Assert.Equal(18.0, LoaderHudMath.DampToward(12.0, target, 1.0, Math.Log(2.0)), 12);
    }

    // 2) Color interpolation and state semantics.
    [Fact]
    public void LerpColorInterpolatesAndClampsT()
    {
        RgbColor red = new RgbColor(1, 0, 0);
        RgbColor green = new RgbColor(0, 1, 0);

        AssertRgb(LoaderHudMath.LerpColor(red, green, 0.25), 0.75, 0.25, 0.0);
        AssertRgb(LoaderHudMath.LerpColor(red, green, 0.0), 1.0, 0.0, 0.0);
        AssertRgb(LoaderHudMath.LerpColor(red, green, 1.0), 0.0, 1.0, 0.0);
        AssertRgb(LoaderHudMath.LerpColor(red, green, -2.0), 1.0, 0.0, 0.0);
        AssertRgb(LoaderHudMath.LerpColor(red, green, 2.0), 0.0, 1.0, 0.0);
        AssertRgb(LoaderHudMath.LerpColor(red, green, double.NaN), 1.0, 0.0, 0.0);
    }

    [Fact]
    public void RgbColorClampsComponentsToUnitRange()
    {
        RgbColor color = new RgbColor(2.0, -1.0, 0.5);
        AssertRgb(color, 1.0, 0.0, 0.5);
    }

    // Configured white stays white until one third left, then fades linearly to red at zero.
    [Theory]
    [InlineData(0.0, 1.0, 0.0, 0.0)]
    [InlineData(0.1, 1.0, 0.3, 0.3)]
    [InlineData(0.2, 1.0, 0.6, 0.6)]
    [InlineData(0.3333333333333333, 1.0, 1.0, 1.0)]
    [InlineData(0.5, 1.0, 1.0, 1.0)]
    [InlineData(1.0, 1.0, 1.0, 1.0)]
    [InlineData(-1.0, 1.0, 0.0, 0.0)]
    [InlineData(double.NaN, 1.0, 0.0, 0.0)]
    public void RemainingTimeColorFadesConfiguredColorToRed(
        double remaining, double expectedR, double expectedG, double expectedB)
    {
        AssertRgb(
            LoaderHudMath.RemainingTimeColor(new RgbColor(1.0, 1.0, 1.0), remaining),
            expectedR, expectedG, expectedB);
    }

    [Fact]
    public void RemainingTimeColorStartsFromConfiguredColorAndEndsPureRed()
    {
        var green = new RgbColor(0.0, 1.0, 0.0);
        AssertRgb(LoaderHudMath.RemainingTimeColor(green, 0.5), 0.0, 1.0, 0.0);
        AssertRgb(LoaderHudMath.RemainingTimeColor(green, 1.0 / 3.0), 0.0, 1.0, 0.0);
        AssertRgb(LoaderHudMath.RemainingTimeColor(green, 1.0 / 6.0), 0.5, 0.5, 0.0);
        AssertRgb(LoaderHudMath.RemainingTimeColor(green, 0.0), 1.0, 0.0, 0.0);
    }

    // Alignment 0 -> red, 0.5 -> yellow, 1 -> green: R = 1-a, G = a, B = 0.
    [Theory]
    [InlineData(0.0, 1.0, 0.0, 0.0)]
    [InlineData(0.25, 0.75, 0.25, 0.0)]
    [InlineData(0.5, 0.5, 0.5, 0.0)]
    [InlineData(1.0, 0.0, 1.0, 0.0)]
    [InlineData(-1.0, 1.0, 0.0, 0.0)]
    [InlineData(2.0, 0.0, 1.0, 0.0)]
    [InlineData(double.NaN, 1.0, 0.0, 0.0)]
    public void AimAlignmentColorShiftsFromRedToGreen(
        double alignment, double expectedR, double expectedG, double expectedB)
    {
        AssertRgb(LoaderHudMath.AimAlignmentColor(alignment), expectedR, expectedG, expectedB);
    }

    // 3) Dot product / direction agreement.
    [Theory]
    [InlineData(3.0, 4.0, 0.0, 1.0, 0.0, 0.0, 0.6)]   // 3/5
    [InlineData(0.0, 0.0, 7.0, 0.0, 0.0, 2.0, 1.0)]   // parallel, different lengths
    [InlineData(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0)]   // perpendicular
    [InlineData(3.0, 4.0, 0.0, -1.0, 0.0, 0.0, -0.6)] // opposite
    [InlineData(0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0)]   // zero velocity
    [InlineData(1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)]   // zero aim
    [InlineData(double.NaN, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0)]
    public void DirectionDotNormalizesBothInputs(
        double vx, double vy, double vz, double ax, double ay, double az, double expected)
    {
        double actual = LoaderHudMath.DirectionDot(new Vec3(vx, vy, vz), new Vec3(ax, ay, az));
        Assert.Equal(expected, actual, 12);
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, 60.0)]
    [InlineData(0.0, 90.0)]
    [InlineData(-0.5, 120.0)]
    [InlineData(-1.0, 180.0)]
    [InlineData(2.0, 0.0)]   // clamped
    [InlineData(-2.0, 180.0)] // clamped
    public void DotMapsToAngleInDegrees(double dot, double expectedDegrees)
    {
        Assert.Equal(expectedDegrees, LoaderHudMath.AngleDegrees(dot), 12);
    }

    [Theory]
    [InlineData(1.0, 100.0)]
    [InlineData(0.6, 60.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(-0.6, 0.0)] // aiming away is 0 percent, never negative
    [InlineData(double.NaN, 0.0)]
    [InlineData(2.0, 100.0)]
    public void DotMapsToClampedAlignmentPercent(double dot, double expectedPercent)
    {
        Assert.Equal(expectedPercent, LoaderHudMath.AlignmentPercent(dot), 12);
    }

    [Fact]
    public void ThreeFourVelocityAgainstUnitXIsSixtyPercentAndFiftyThreeDegrees()
    {
        double dot = LoaderHudMath.DirectionDot(new Vec3(3, 4, 0), new Vec3(1, 0, 0));
        Assert.Equal(0.6, dot, 12);
        Assert.Equal(60.0, LoaderHudMath.AlignmentPercent(dot), 12);
        Assert.Equal(53.13010235415598, LoaderHudMath.AngleDegrees(dot), 12);
    }

    // 4) Grapple energy: 0.5*m*v^2 + m*g*h + m*g*rope + m*slam*h.
    [Theory]
    [InlineData(2.0, 3.0, 10.0, 5.0, 9.8, 20.0, 703.0)] // 9 + 196 + 98 + 400
    [InlineData(1.0, 0.0, 2.0, 1.0, 10.0, 5.0, 40.0)]   // 0 + 20 + 10 + 10
    [InlineData(1.0, 2.0, 0.5, 0.0, 10.0, 0.0, 7.0)]    // 2 + 5
    [InlineData(1.0, 0.0, 10.0, 0.0, 0.0, 0.0, 0.0)]
    [InlineData(0.0, 100.0, 100.0, 100.0, 10.0, 10.0, 0.0)] // zero mass
    [InlineData(1.0, 0.0, 2.0, 1.0, -10.0, -5.0, 40.0)] // signs ignored: 20 + 10 + 10
    [InlineData(double.NaN, 0.0, 2.0, 1.0, 10.0, 0.0, 0.0)] // invalid mass
    public void GrappleEnergyAddsKineticGravityRopeAndSlamTerms(
        double mass, double speed, double height, double ropeLength,
        double gravity, double slamAcceleration, double expected)
    {
        double actual = LoaderHudMath.GrappleEnergy(
            mass, speed, height, ropeLength, gravity, slamAcceleration);
        Assert.Equal(expected, actual, 9);
    }

    // No ground under the player: invalid or below 0.5 m height falls back to 0.5 m.
    [Theory]
    [InlineData(0.0, 5.0)]
    [InlineData(0.4, 5.0)]
    [InlineData(-100.0, 5.0)]
    [InlineData(double.NaN, 5.0)]
    [InlineData(double.PositiveInfinity, 5.0)]
    [InlineData(0.5, 5.0)]
    [InlineData(0.6, 6.0)]
    [InlineData(10.0, 100.0)]
    public void GrappleEnergyProtectsAgainstMissingOrTinyHeight(
        double height, double expected)
    {
        double actual = LoaderHudMath.GrappleEnergy(
            mass: 1.0, speed: 0.0, height: height, ropeLength: 0.0,
            gravity: 10.0, slamAcceleration: 0.0);
        Assert.Equal(expected, actual, 9);
    }

    // 5) Remaining grapple lifetime.
    [Theory]
    [InlineData(3.0, 1.0, 2.0)]
    [InlineData(2.5, 1.0, 1.5)]
    [InlineData(3.0, 3.0, 0.0)]
    [InlineData(3.0, 4.0, 0.0)]
    [InlineData(3.0, -1.0, 3.0)]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(-2.0, 0.0, 0.0)]
    [InlineData(double.NaN, 1.0, 0.0)]
    [InlineData(3.0, double.NaN, 3.0)]
    [InlineData(3.0, double.PositiveInfinity, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0, 0.0)]
    public void RemainingSecondsClampsAtZero(
        double lifetime, double elapsed, double expected)
    {
        Assert.Equal(expected, LoaderHudMath.RemainingSeconds(lifetime, elapsed), 12);
    }

    // 6) Punch records: one entry per punch, newest first, slam-before-hit dims.
    [Theory]
    [InlineData(0.9, 1.0, true)]
    [InlineData(1.0, 1.0, true)]   // same tick counts as before
    [InlineData(1.1, 1.0, false)]
    [InlineData(-1.0, 1.0, false)] // no slam recorded
    [InlineData(double.NaN, 1.0, false)]
    [InlineData(0.0, double.NaN, false)]
    public void ThunderslamFirstRequiresSlamNoLaterThanHit(
        double slamSeconds, double hitSeconds, bool expected)
    {
        Assert.Equal(expected, LoaderHudMath.IsThunderslamFirst(slamSeconds, hitSeconds));
    }

    [Fact]
    public void PunchRecordsDeduplicateMultiTargetHitsAndSortNewestFirst()
    {
        PunchHit[] hits =
        {
            new PunchHit(punchId: 1, hitSeconds: 10.0, slamSeconds: -1.0, speed: 20.0, aimDot: 0.5),
            new PunchHit(punchId: 2, hitSeconds: 11.0, slamSeconds: 10.5, speed: 30.0, aimDot: 0.8),
            new PunchHit(punchId: 1, hitSeconds: 10.2, slamSeconds: -1.0, speed: 25.0, aimDot: 0.7),
            new PunchHit(punchId: 3, hitSeconds: 9.0, slamSeconds: 9.5, speed: 40.0, aimDot: -0.2),
        };

        PunchRecord[] records = LoaderHudMath.BuildPunchRecords(hits, 10);

        Assert.Equal(3, records.Length);
        Assert.Equal(30.0, records[0].Speed, 12); // punch 2, newest
        Assert.Equal(0.8, records[0].AimDot, 12);
        Assert.True(records[0].SlammedFirst);
        Assert.Equal(20.0, records[1].Speed, 12); // punch 1, first target only
        Assert.Equal(0.5, records[1].AimDot, 12);
        Assert.False(records[1].SlammedFirst);
        Assert.Equal(40.0, records[2].Speed, 12); // punch 3, slam came after the hit
        Assert.Equal(0.0, records[2].AimDot, 12); // dot clamped to 0..1
        Assert.False(records[2].SlammedFirst);
    }

    [Fact]
    public void PunchRecordsKeepOnlyNewestN()
    {
        PunchHit[] hits =
        {
            new PunchHit(1, 1.0, -1.0, 10.0, 0.1),
            new PunchHit(2, 2.0, -1.0, 20.0, 0.2),
            new PunchHit(3, 3.0, -1.0, 30.0, 0.3),
        };

        PunchRecord[] records = LoaderHudMath.BuildPunchRecords(hits, 2);

        Assert.Equal(2, records.Length);
        Assert.Equal(30.0, records[0].Speed, 12);
        Assert.Equal(20.0, records[1].Speed, 12);
    }

    [Fact]
    public void PunchRecordsPreserveInputOrderForEqualHitTimes()
    {
        PunchHit[] hits =
        {
            new PunchHit(7, 5.0, -1.0, 70.0, 0.7),
            new PunchHit(8, 5.0, -1.0, 80.0, 0.8),
            new PunchHit(9, 6.0, -1.0, 90.0, 0.9),
        };

        PunchRecord[] records = LoaderHudMath.BuildPunchRecords(hits, 10);

        Assert.Equal(3, records.Length);
        Assert.Equal(90.0, records[0].Speed, 12);
        Assert.Equal(70.0, records[1].Speed, 12);
        Assert.Equal(80.0, records[2].Speed, 12);
    }

    [Fact]
    public void PunchRecordsClampAimDotToDisplayRange()
    {
        PunchHit[] hits =
        {
            new PunchHit(1, 1.0, -1.0, 10.0, 1.7),
            new PunchHit(2, 2.0, -1.0, 20.0, -0.3),
        };

        PunchRecord[] records = LoaderHudMath.BuildPunchRecords(hits, 10);

        Assert.Equal(2, records.Length);
        Assert.Equal(0.0, records[0].AimDot, 12); // punch 2 is newest, dot -0.3 clamped to 0
        Assert.Equal(1.0, records[1].AimDot, 12); // punch 1, dot 1.7 clamped to 1
    }

    [Fact]
    public void PunchRecordsSkipInvalidEntriesAndEmptyInputs()
    {
        PunchHit[] hits =
        {
            new PunchHit(1, double.NaN, -1.0, 10.0, 0.5),
            new PunchHit(2, 2.0, -1.0, double.PositiveInfinity, 0.5),
            new PunchHit(3, 3.0, -1.0, 30.0, 0.5),
        };

        PunchRecord[] records = LoaderHudMath.BuildPunchRecords(hits, 10);
        Assert.Single(records);
        Assert.Equal(30.0, records[0].Speed, 12);

        Assert.Empty(LoaderHudMath.BuildPunchRecords(hits, 0));
        Assert.Empty(LoaderHudMath.BuildPunchRecords(hits, -1));
        Assert.Empty(LoaderHudMath.BuildPunchRecords(null, 10));
        Assert.Empty(LoaderHudMath.BuildPunchRecords(Array.Empty<PunchHit>(), 10));
    }

    // 7) Anchor relative offset -> pixel offset, scaled by canvas size and UI scale.
    [Theory]
    [InlineData(0.05, -0.02, 1920.0, 1080.0, 1.5, 144.0, -32.4)]
    [InlineData(0.05, -0.02, 1920.0, 1080.0, 1.0, 96.0, -21.6)]
    [InlineData(0.05, -0.02, 2560.0, 1440.0, 1.0, 128.0, -28.8)]
    [InlineData(0.1, 0.1, 1000.0, 500.0, 2.0, 200.0, 100.0)]
    [InlineData(-0.1, 0.1, 1000.0, 500.0, 1.0, -100.0, 50.0)]
    [InlineData(0.0, 0.0, 1920.0, 1080.0, 1.0, 0.0, 0.0)]
    public void AnchorOffsetScalesWithCanvasSizeAndUiScale(
        double relativeX, double relativeY, double canvasWidth, double canvasHeight,
        double uiScale, double expectedX, double expectedY)
    {
        AssertVec2(
            LoaderHudMath.AnchorPixelOffset(
                relativeX, relativeY, canvasWidth, canvasHeight, uiScale),
            expectedX, expectedY);
    }

    [Theory]
    [InlineData(0.05, -0.02, 0.0, 1080.0, 1.0)]
    [InlineData(0.05, -0.02, 1920.0, 0.0, 1.0)]
    [InlineData(0.05, -0.02, 1920.0, 1080.0, 0.0)]
    [InlineData(0.05, -0.02, 1920.0, 1080.0, -1.0)]
    [InlineData(0.05, -0.02, double.NaN, 1080.0, 1.0)]
    [InlineData(0.05, -0.02, 1920.0, 1080.0, double.NaN)]
    [InlineData(double.NaN, -0.02, 1920.0, 1080.0, 1.0)]
    public void AnchorOffsetRejectsInvalidCanvasOrScale(
        double relativeX, double relativeY, double canvasWidth, double canvasHeight,
        double uiScale)
    {
        AssertVec2(
            LoaderHudMath.AnchorPixelOffset(
                relativeX, relativeY, canvasWidth, canvasHeight, uiScale),
            0.0, 0.0);
    }
}
