using System;
using Xunit;

namespace RoR2LatencyMod.Tests;

/// <summary>
/// Pure-logic tests for the two grapple HUDs. Game/Unity reads stay in the Render path;
/// only the Unity-free math is exercised here.
/// </summary>
public class LoaderHudGrappleTests
{
    // Remaining fraction = RemainingSeconds(lifetime, elapsed) / lifetime, clamped by the
    // existing pure helper; non-positive or non-finite lifetimes mean "nothing left".
    [Theory]
    [InlineData(5.0, 0.0, 1.0)]
    [InlineData(5.0, 2.0, 0.6)]
    [InlineData(5.0, 4.9, 0.02)]
    [InlineData(5.0, 5.0, 0.0)]
    [InlineData(5.0, 9.0, 0.0)]
    [InlineData(5.0, -1.0, 1.0)]
    [InlineData(5.0, double.NaN, 1.0)]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(-5.0, 1.0, 0.0)]
    [InlineData(double.NaN, 1.0, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0, 0.0)]
    public void RemainingFractionStaysInUnitRange(
        double lifetime, double elapsed, double expected)
    {
        Assert.Equal(
            expected,
            LoaderHudGrappleDurationMath.RemainingFraction(lifetime, elapsed),
            12);
    }

    // Red only below the same 1/3 turn point LoaderHudMath.RemainingTimeColor uses.
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.3334, false)]
    [InlineData(1.0 / 3.0, false)]
    [InlineData(0.33, true)]
    [InlineData(0.0, true)]
    [InlineData(-1.0, false)]
    [InlineData(double.NaN, false)]
    public void DangerOnlyBelowOneThirdRemaining(double fraction, bool expected)
    {
        Assert.Equal(expected, LoaderHudGrappleDurationMath.IsDanger(fraction));
    }

    [Theory]
    [InlineData(3.456, "3.46s")]
    [InlineData(1.2, "1.20s")]
    [InlineData(0.0, "0.00s")]
    [InlineData(-1.0, "0.00s")]
    [InlineData(double.NaN, "0.00s")]
    [InlineData(double.PositiveInfinity, "0.00s")]
    public void FormatSecondsIsFixedWidthAndSafe(double seconds, string expected)
    {
        Assert.Equal(expected, LoaderHudGrappleDurationMath.FormatSeconds(seconds));
    }

    // End-to-end pure chain behind "time reaches 0 turns red": fraction -> RemainingTimeColor.
    [Fact]
    public void DangerChainFadesConfiguredColorToRedAtZero()
    {
        var white = new RgbColor(1.0, 1.0, 1.0);
        double noneLeft = LoaderHudGrappleDurationMath.RemainingFraction(5.0, 5.0);
        RgbColor red = LoaderHudMath.RemainingTimeColor(white, noneLeft);
        Assert.Equal(1.0, red.R, 12);
        Assert.Equal(0.0, red.G, 12);
        Assert.Equal(0.0, red.B, 12);

        double oneTenthLeft = LoaderHudGrappleDurationMath.RemainingFraction(10.0, 9.0);
        RgbColor fading = LoaderHudMath.RemainingTimeColor(white, oneTenthLeft);
        Assert.Equal(1.0, fading.R, 12);
        Assert.Equal(0.3, fading.G, 12);
        Assert.Equal(0.3, fading.B, 12);

        double turnPoint = LoaderHudGrappleDurationMath.DangerFraction;
        RgbColor configured = LoaderHudMath.RemainingTimeColor(white, turnPoint);
        Assert.Equal(1.0, configured.R, 12);
        Assert.Equal(1.0, configured.G, 12);
        Assert.Equal(1.0, configured.B, 12);
    }

    // aim origin (0,0,0) -> hook (3,4,0) with no offset: classic 3-4-5.
    [Theory]
    [InlineData(0.0, 0.0, 0.0, 3.0, 4.0, 0.0, 0.0, 0.0, 1.0, 0.0, 5.0)]
    // transform.up * normalOffset: hook (3,4,0) + up(0,0,1)*2 -> sqrt(29).
    [InlineData(0.0, 0.0, 0.0, 3.0, 4.0, 0.0, 0.0, 0.0, 1.0, 2.0, 5.385164807134504)]
    // Negative offset subtracts along up: (0,0,5) + (0,0,1)*-2 -> 3.
    [InlineData(0.0, 0.0, 0.0, 0.0, 0.0, 5.0, 0.0, 0.0, 1.0, -2.0, 3.0)]
    // up is not normalized (decompiled code multiplies transform.up directly).
    [InlineData(0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 2.0, 1.5, 4.0)]
    // Negative coordinates: (-1,-2,-3) -> (2,2,1) = sqrt(9+16+16).
    [InlineData(-1.0, -2.0, -3.0, 2.0, 2.0, 1.0, 0.0, 0.0, 0.0, 0.0, 6.4031242374328485)]
    public void HookDistanceAddsUpOffsetThenMeasuresToAimOrigin(
        double ax, double ay, double az,
        double hx, double hy, double hz,
        double ux, double uy, double uz,
        double offset, double expected)
    {
        double actual = LoaderHudGrappleLengthMath.HookDistance(
            new Vec3(ax, ay, az), new Vec3(hx, hy, hz), new Vec3(ux, uy, uz), offset);
        Assert.Equal(expected, actual, 12);
    }

    [Fact]
    public void HookDistanceRejectsNonFiniteInputs()
    {
        Vec3 origin = new Vec3(0.0, 0.0, 0.0);
        Vec3 hook = new Vec3(1.0, 2.0, 3.0);
        Vec3 up = new Vec3(0.0, 0.0, 1.0);

        Assert.True(double.IsNaN(LoaderHudGrappleLengthMath.HookDistance(
            new Vec3(double.NaN, 0.0, 0.0), hook, up, 0.0)));
        Assert.True(double.IsNaN(LoaderHudGrappleLengthMath.HookDistance(
            origin, new Vec3(0.0, double.PositiveInfinity, 0.0), up, 0.0)));
        Assert.True(double.IsNaN(LoaderHudGrappleLengthMath.HookDistance(
            origin, hook, new Vec3(0.0, 0.0, double.NaN), 0.0)));
        Assert.True(double.IsNaN(LoaderHudGrappleLengthMath.HookDistance(
            origin, hook, up, double.NaN)));
        Assert.True(double.IsNaN(LoaderHudGrappleLengthMath.HookDistance(
            origin, hook, up, double.PositiveInfinity)));
    }

    [Theory]
    [InlineData(42.0, "42.0 m")]
    [InlineData(42.34, "42.3 m")]
    [InlineData(42.36, "42.4 m")]
    [InlineData(0.0, "0.0 m")]
    [InlineData(-3.0, "0.0 m")]
    [InlineData(double.NaN, "0.0 m")]
    [InlineData(double.PositiveInfinity, "0.0 m")]
    public void FormatMetersIsFixedWidthAndSafe(double meters, string expected)
    {
        Assert.Equal(expected, LoaderHudGrappleLengthMath.FormatMeters(meters));
    }
}
