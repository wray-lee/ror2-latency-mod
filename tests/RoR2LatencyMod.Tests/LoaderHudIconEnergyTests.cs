using System;
using Xunit;

namespace RoR2LatencyMod.Tests;

/// <summary>
/// Hand-computed checks for the two HUD data rules added in this task:
/// velocity-icon fade/tint and grapple-energy formula + void-height protection.
/// </summary>
public class LoaderHudIconEnergyTests
{
    private static void AssertRgb(RgbColor actual, double r, double g, double b)
    {
        Assert.Equal(r, actual.R, 12);
        Assert.Equal(g, actual.G, 12);
        Assert.Equal(b, actual.B, 12);
    }

    // Smoothstep on speed/20: t=0.25 -> 0.15625, t=0.5 -> 0.5, t=0.75 -> 0.84375.
    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(5.0, 0.15625)]
    [InlineData(10.0, 0.5)]
    [InlineData(15.0, 0.84375)]
    [InlineData(20.0, 1.0)]
    [InlineData(100.0, 1.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void VelocityIconFadesInWithSpeed(double speed, double expected)
    {
        Assert.Equal(expected, LoaderVelocityIconMath.Visibility(speed), 12);
    }

    // Aim dot -1 -> red, 0 -> half red/half green (yellow), 1 -> green.
    [Theory]
    [InlineData(-1.0, 1.0, 0.0, 0.0)]
    [InlineData(0.0, 0.5, 0.5, 0.0)]
    [InlineData(1.0, 0.0, 1.0, 0.0)]
    [InlineData(2.0, 0.0, 1.0, 0.0)]
    [InlineData(-2.0, 1.0, 0.0, 0.0)]
    [InlineData(double.NaN, 0.5, 0.5, 0.0)]
    public void VelocityIconGetsGreenerAsAimAlignsWithVelocity(
        double dot, double expectedR, double expectedG, double expectedB)
    {
        AssertRgb(LoaderVelocityIconMath.AimColor(dot), expectedR, expectedG, expectedB);
    }

    // Independent hand calculation, m=2, v=3, h=10, rope=4, g=9.8, slam=6:
    // kinetic 0.5*2*9 = 9; gravity 2*9.8*10 = 196; rope 2*9.8*4 = 78.4;
    // slam 2*6*10 = 120; total = 403.4 J.
    [Fact]
    public void GrappleEnergyMatchesHandCalculatedFourTerms()
    {
        Assert.Equal(
            403.4,
            LoaderGrappleEnergyMath.Estimate(2.0, 3.0, 10.0, 4.0, 9.8, 6.0),
            9);
    }

    // Each term isolated for a second hand check.
    [Theory]
    [InlineData(2.0, 3.0, 10.0, 0.0, 0.0, 0.0, 9.0)]    // kinetic only
    [InlineData(2.0, 0.0, 10.0, 0.0, 9.8, 0.0, 196.0)] // gravity only
    [InlineData(2.0, 0.0, 10.0, 0.0, 0.0, 6.0, 120.0)] // slam only
    public void GrappleEnergyTermsAreIndividuallyCorrect(
        double mass, double speed, double height, double rope,
        double gravity, double slam, double expected)
    {
        Assert.Equal(
            expected,
            LoaderGrappleEnergyMath.Estimate(mass, speed, height, rope, gravity, slam),
            9);
    }

    // Void protection: NaN/infinite/low heights use 0.5 m. m=2, v=3, rope=4, g=9.8, slam=6:
    // 9 + 2*9.8*0.5 + 78.4 + 2*6*0.5 = 103.2 J.
    [Theory]
    [InlineData(0.1)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GrappleEnergyUsesSafeHeightInVoidAndInvalidReadings(double height)
    {
        Assert.Equal(
            103.2,
            LoaderGrappleEnergyMath.Estimate(2.0, 3.0, height, 4.0, 9.8, 6.0),
            9);
    }

    [Theory]
    [InlineData(10.0, 10.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.499, 0.5)]
    [InlineData(0.0, 0.5)]
    [InlineData(-1.0, 0.5)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(double.PositiveInfinity, 0.5)]
    [InlineData(double.NegativeInfinity, 0.5)]
    public void SafeHeightClampsInvalidOrVeryLowHeight(double raw, double expected)
    {
        Assert.Equal(expected, LoaderGrappleEnergyMath.SafeHeight(raw), 12);
    }

    [Theory]
    [InlineData(123.4, 123.4)]
    [InlineData(-1.0, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(1e12, LoaderGrappleEnergyMath.MaximumDisplayJoules)]
    [InlineData(double.PositiveInfinity, LoaderGrappleEnergyMath.MaximumDisplayJoules)]
    public void DisplayJoulesAreClamped(double raw, double expected)
    {
        Assert.Equal(expected, LoaderGrappleEnergyMath.ClampJoules(raw), 12);
    }

    [Theory]
    [InlineData(403.4, "403 J")]
    [InlineData(1500.0, "1.5 kJ")]
    [InlineData(2000000.0, "2.00 MJ")]
    [InlineData(-1.0, "0 J")]
    [InlineData(double.NaN, "0 J")]
    public void EnergyFormattingIsStable(double joules, string expected)
    {
        Assert.Equal(expected, LoaderGrappleEnergyMath.FormatJoules(joules));
    }
}
