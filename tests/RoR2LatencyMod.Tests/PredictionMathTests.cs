using Xunit;

namespace RoR2LatencyMod.Tests;

public class PredictionMathTests
{
    [Theory]
    [InlineData(20.0, 0.0, 0.0)]     // no latency
    [InlineData(20.0, 0.075, 1.5)]   // 20 * 0.075
    [InlineData(20.0, 0.250, 5.0)]   // cap boundary: 20 * 0.25
    [InlineData(20.0, 0.500, 5.0)]   // beyond cap: still 20 * 0.25
    [InlineData(20.0, -0.100, 0.0)]  // negative latency cannot move backwards
    public void StartLeadIsCappedAtQuarterSecondOfDashTravel(
        double dashSpeed, double oneWaySeconds, double expectedMeters)
    {
        Assert.Equal(expectedMeters, PredictionMath.StartLead(dashSpeed, oneWaySeconds), 12);
    }

    [Theory]
    [InlineData(0.000, 1.0)]   // floor wins: 3 * 0 = 0 -> 1.0
    [InlineData(0.150, 1.0)]   // 3 * 0.15 = 0.45 -> 1.0
    [InlineData(0.250, 1.0)]   // 3 * 0.25 = 0.75 -> 1.0
    [InlineData(0.340, 1.02)]  // 3 * 0.34 = 1.02
    [InlineData(0.400, 1.2)]   // 3 * 0.4
    [InlineData(1.000, 3.0)]   // 3 * 1.0, at the 3 s default cap
    [InlineData(2.000, 3.0)]   // beyond cap
    [InlineData(4.000, 3.0)]   // RTT spike: cap still bounds the animation
    public void StuckTimeoutIsThreeRttWithOneSecondFloor(
        double roundTripSeconds, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, PredictionMath.StuckTimeout(roundTripSeconds), 12);
    }

    [Theory]
    [InlineData(2.000, 1.500, 1.500)] // configured cap below the automatic value
    [InlineData(2.000, 5.000, 5.000)] // 3 * 2 = 6.0, configured cap wins
    [InlineData(0.000, 1.500, 1.0)]   // floor wins
    [InlineData(0.000, 0.500, 0.500)] // user can lower the cap below the floor
    [InlineData(2.000, 0.100, 0.500)] // cap is clamped to the config range minimum
    public void StuckTimeoutHonorsAConfiguredMaximum(
        double roundTripSeconds, double maxSeconds, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, PredictionMath.StuckTimeout(roundTripSeconds, maxSeconds), 12);
    }

    [Theory]
    [InlineData(0.0, 0.0, true)]      // seat exactly at the ghost
    [InlineData(-2.0, 0.0, true)]     // within the interpolation-lag tolerance
    [InlineData(-2.01, 0.0, false)]   // still visibly behind: keep holding
    [InlineData(5.0, 0.0, true)]      // vehicle already passed the ghost
    [InlineData(0.0, 2.5, true)]      // lateral tolerance boundary
    [InlineData(0.0, 2.51, false)]    // off the dash line: not a catch-up
    public void HandoffCatchesUpOnlyWhenTheSeatReachesTheGhost(
        double alongMeters, double lateralMeters, bool expected)
    {
        Assert.Equal(expected, PredictionMath.HandoffCaughtUp(alongMeters, lateralMeters));
    }

    [Fact]
    public void HandoffDistanceLimitCoversTheGhostsMaximumBaseTravel()
    {
        // Observed in the 2026-10-23 room log: VehicleShieldDashLight speedCoefficient=50 m/s,
        // duration=0.20 s; the ghost may travel the 0.25 s lead cap plus the dash.
        const double baseDashSpeed = 50.0;
        const double dashSeconds = 0.2;
        double maximumGhostTravel = (PredictionMath.StartLeadCapSeconds + dashSeconds) * baseDashSpeed;

        Assert.True(
            PredictionMath.MaxHandoffDistanceMeters
            >= maximumGhostTravel + PredictionMath.HandoffMarginMeters,
            $"limit {PredictionMath.MaxHandoffDistanceMeters} must cover {maximumGhostTravel} m + margin");
        Assert.Equal(30.0, PredictionMath.MaxHandoffDistanceMeters, 12);
    }

    [Theory]
    [InlineData(30.0, 0.0, false)]      // absolute limit boundary
    [InlineData(30.01, 0.0, true)]      // beyond the absolute limit: teleport
    [InlineData(40.0, 42.5, false)]     // fast items raise the relative limit to 45 m
    [InlineData(45.0, 42.5, false)]     // relative limit boundary
    [InlineData(45.01, 42.5, true)]     // beyond the relative limit: teleport
    public void HandoffTeleportCheckScalesWithPredictedTravel(
        double gapMeters, double predictedTravelMeters, bool expected)
    {
        Assert.Equal(expected, PredictionMath.HandoffIsTeleport(gapMeters, predictedTravelMeters));
    }

    [Theory]
    [InlineData(0.0, 0.500)]   // floor
    [InlineData(0.2, 0.500)]   // 2 * 0.2 = 0.4 -> floor
    [InlineData(0.3, 0.600)]   // 2 * 0.3
    [InlineData(1.0, 2.0)]     // 2 * 1.0
    [InlineData(2.0, 3.0)]     // capped by the fallback maximum
    public void HoldTimeoutIsTwiceRttWithHalfSecondFloor(
        double roundTripSeconds, double expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            PredictionMath.HoldTimeout(roundTripSeconds, PredictionMath.MaxStuckTimeoutSeconds),
            12);
    }

    [Theory]
    // Predicted-hook cleanup predicate. Timeout = HoldTimeout(rtt, cap) = min(max(2*rtt, 0.5), clamp(cap, 0.5, 60)).
    [InlineData(true, false, false, 0.10, 0.356, 3.0, PredictedHookDropReason.Handoff)]      // handoff wins at any age
    [InlineData(true, true, false, 99.0, 0.356, 3.0, PredictedHookDropReason.Handoff)]       // handoff beats exit and timeout
    [InlineData(false, true, false, 0.10, 0.356, 3.0, PredictedHookDropReason.FireHookExit)] // skill exit before the window
    [InlineData(true, false, true, 0.10, 0.356, 3.0, PredictedHookDropReason.None)]          // already destroyed: never twice
    [InlineData(false, true, true, 99.0, 0.356, 3.0, PredictedHookDropReason.None)]          // dedupe beats every reason
    [InlineData(false, false, false, 0.711, 0.356, 3.0, PredictedHookDropReason.None)]       // 2 * 0.356 = 0.712, just under
    [InlineData(false, false, false, 0.712, 0.356, 3.0, PredictedHookDropReason.Timeout)]    // boundary: age == 2 * RTT
    [InlineData(false, false, false, 0.499, 0.0, 3.0, PredictedHookDropReason.None)]         // 2 * 0 = 0 -> floor 0.5, just under
    [InlineData(false, false, false, 0.500, 0.0, 3.0, PredictedHookDropReason.Timeout)]      // boundary: 0.5 s floor
    [InlineData(false, false, false, 2.999, 2.0, 3.0, PredictedHookDropReason.None)]         // 2 * 2 = 4 -> capped at 3.0
    [InlineData(false, false, false, 3.000, 2.0, 3.0, PredictedHookDropReason.Timeout)]      // boundary: cap 3.0
    [InlineData(false, false, false, 0.600, 1.0, 0.5, PredictedHookDropReason.Timeout)]      // min(max(2,0.5),0.5)=0.5
    [InlineData(false, false, false, 0.300, 1.0, 0.5, PredictedHookDropReason.None)]         // 0.3 < 0.5
    [InlineData(false, false, false, 0.300, -1.0, 3.0, PredictedHookDropReason.None)]        // negative RTT -> floor 0.5
    [InlineData(false, false, false, double.NaN, 0.0, 3.0, PredictedHookDropReason.None)]    // NaN age counts as 0
    public void PredictedHookDropPrefersHandoffThenExitThenCappedTimeout(
        bool handedOff,
        bool fireHookExited,
        bool alreadyDestroyed,
        double ageSeconds,
        double roundTripSeconds,
        double maxSeconds,
        PredictedHookDropReason expected)
    {
        Assert.Equal(
            expected,
            PredictionMath.PredictedHookDrop(
                handedOff, fireHookExited, alreadyDestroyed, ageSeconds, roundTripSeconds, maxSeconds));
    }

    [Theory]
    [InlineData(0.050, 0.1)]   // below minimum
    [InlineData(0.100, 0.1)]   // minimum boundary
    [InlineData(0.150, 0.15)]  // inside range
    [InlineData(0.200, 0.2)]   // maximum boundary
    [InlineData(0.300, 0.2)]   // above maximum
    public void InterpolationDurationIsClampedToTenthsOfASecond(
        double requestedSeconds, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, PredictionMath.ClampInterpolationDuration(requestedSeconds), 12);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.5, 2.5)]
    [InlineData(0.5, 1.0)]                   // never slower than vanilla
    [InlineData(9.0, 9.0)]
    [InlineData(12.0, 10.0)]                 // clamped to 10x
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void BuffMultiplierIsClamped(double requested, double expected)
    {
        Assert.Equal(expected, PredictionMath.BuffMultiplier(requested), 12);
    }

    [Theory]
    // provideRulerEquivalent × loaderRulerFixEnabled × presence.
    [InlineData(0.222, 0.120, true, false, false, 0.444)]  // default on, no Ruler: 2 * base
    [InlineData(0.222, 0.120, true, true, true, 0.444)]    // default on with Ruler: same deadline, no double
    [InlineData(0.222, 0.120, false, false, false, 0.342)] // off, no Ruler: base + ownExtra
    [InlineData(0.222, 0.120, false, true, true, 0.444)]   // off with Ruler on: its doubling is not cut short
    [InlineData(0.050, 0.120, true, false, false, 0.170)]  // own extra above base wins
    [InlineData(0.050, 0.120, true, true, true, 0.170)]    // and is not stacked twice
    [InlineData(0.222, 0.300, true, true, true, 0.522)]    // base + ownExtra when ownExtra > base
    [InlineData(0.000, 0.120, true, false, false, 0.120)]  // no flight time, only the extra
    [InlineData(0.222, -1.000, false, false, false, 0.222)] // negative extra cannot shorten vanilla
    [InlineData(0.222, 3.900, true, false, false, 4.122)]  // 3.9 s is within the 4 s cap
    [InlineData(0.222, 4.500, true, false, false, 4.222)]  // extra clamped to the 4 s hard cap
    public void FlyDeadlineDefaultsToRulerEquivalentWithoutDoubleStacking(
        double baseFlySeconds,
        double ownExtraSeconds,
        bool provideRulerEquivalent,
        bool loaderRulerFixEnabled,
        bool loaderRulerPresent,
        double expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            PredictionMath.FlyDeadlineSeconds(
                baseFlySeconds, ownExtraSeconds, provideRulerEquivalent,
                loaderRulerFixEnabled, loaderRulerPresent),
            12);
    }

    [Theory]
    // loaderRulerPresent × loaderRulerFixEnabled × our switch. Present-and-off is the user's
    // explicit opt-out of Ruler's doubling, so no formula may add it back.
    [InlineData(0.222, 0.120, true, false, true, 0.342)]   // Ruler installed, fix off: 0.222 + 0.120
    [InlineData(0.222, 0.300, true, false, true, 0.522)]   // own extra 0.300 wins: 0.222 + 0.300
    [InlineData(0.222, 0.120, false, false, true, 0.342)]  // our switch off too: still no doubling
    [InlineData(0.222, 0.120, true, true, true, 0.444)]    // Ruler on: 2 * 0.222 once
    [InlineData(0.222, 0.120, false, true, true, 0.444)]   // our switch off, Ruler on: match its 2x, do not cut
    [InlineData(0.222, 0.120, true, false, false, 0.444)]  // Ruler absent, our default on: 2 * 0.222
    [InlineData(0.222, 0.120, false, false, false, 0.342)] // Ruler absent, our switch off: 0.222 + 0.120
    [InlineData(0.050, 0.120, true, false, true, 0.170)]   // fix off, own extra 0.120: 0.050 + 0.120
    public void FlyDeadlineRespectsLoaderRulerFixOff(
        double baseFlySeconds,
        double ownExtraSeconds,
        bool provideRulerEquivalent,
        bool loaderRulerFixEnabled,
        bool loaderRulerPresent,
        double expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            PredictionMath.FlyDeadlineSeconds(
                baseFlySeconds, ownExtraSeconds, provideRulerEquivalent,
                loaderRulerFixEnabled, loaderRulerPresent),
            12);
    }

    [Theory]
    // One 50 ms tick-alignment slack is always added; the one-way latency is the extra wait.
    [InlineData(0.000, 0.050)]  // 0 + 0.05
    [InlineData(0.150, 0.200)]  // 0.15 + 0.05
    [InlineData(0.500, 0.550)]  // 0.50 + 0.05
    [InlineData(1.200, 1.000)]  // capped at 1 s
    [InlineData(-0.100, 0.050)] // negative latency only leaves the slack
    [InlineData(double.NaN, 0.050)] // no estimate: slack only
    public void StuckSyncGraceCoversOneWayLatencyPlusTickSlack(
        double oneWayLatencySeconds, double expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds, PredictionMath.StuckSyncGraceSeconds(oneWayLatencySeconds), 12);
    }

    [Theory]
    // Default-on equivalent at base 0.222, extra 0.120: 2 * 0.222 = 0.444, plus U 0.150 + 0.05.
    [InlineData(0.222, 0.120, true, false, false, 0.150, 0.644)]
    // Ruler installed but fixed off: 0.222 + 0.120 = 0.342, plus 0.150 + 0.05.
    [InlineData(0.222, 0.120, true, false, true, 0.150, 0.542)]
    // Grace cap: 0.444 + min(1.2 + 0.05, 1.0) = 1.444.
    [InlineData(0.222, 0.120, true, false, false, 1.200, 1.444)]
    public void FlyDeadlineWithStuckGraceAddsTheSyncWindow(
        double baseFlySeconds,
        double ownExtraSeconds,
        bool provideRulerEquivalent,
        bool loaderRulerFixEnabled,
        bool loaderRulerPresent,
        double oneWayLatencySeconds,
        double expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            PredictionMath.FlyDeadlineWithStuckGrace(
                baseFlySeconds, ownExtraSeconds, provideRulerEquivalent,
                loaderRulerFixEnabled, loaderRulerPresent, oneWayLatencySeconds),
            12);
    }

    [Theory]
    // The caller reaches this decision after the fallback timeout and, for ReturnState or a
    // released stuck hook, after the return/release grace; that protection is covered below.
    // hookPresent, sawHook, hookGone, stuck, keyHeld, hookStateName, flyWithinDeadline, expected
    [InlineData(true, true, false, false, true, "ReturnState", false, "ReturnState not destroyed")]
    [InlineData(true, true, false, true, true, "ReturnState", false, "ReturnState not destroyed")]
    [InlineData(true, true, false, true, true, "FlyState", true, null)]  // synced hit + held: never force
    [InlineData(true, true, false, false, true, "FlyState", true, null)] // inside graced deadline: protect
    [InlineData(true, true, false, false, true, "FlyState", false, "not stuck and not returning")]
    [InlineData(true, true, false, true, false, "FlyState", true, "released while stuck, no return")]
    [InlineData(false, false, false, false, true, "none", false, "never got hookInstance")]
    [InlineData(false, true, true, false, true, "ReturnState", false,
        "hook destroyed but FireHook did not exit")]
    [InlineData(false, true, false, false, true, "none", false, null)] // reference not seen yet: wait
    public void HookFallbackWaitsForStuckSyncAndProtectsFlyState(
        bool hookPresent,
        bool sawHook,
        bool hookGone,
        bool stuck,
        bool keyHeld,
        string hookStateName,
        bool flyWithinDeadline,
        string expectedReason)
    {
        Assert.Equal(
            expectedReason,
            PredictionMath.HookFallbackReason(
                hookPresent, sawHook, hookGone, stuck, keyHeld, hookStateName, flyWithinDeadline));
    }

    [Theory]
    // Healthy return after a long hold: FireHook age 1.5 s is past the 1.0 s fallback timeout,
    // but ReturnState itself is only 0.1 s old, so it must not be forced yet.
    [InlineData(true, false, true, "ReturnState", 0.10, 1.00, 1.00, true)]
    // The return itself is overdue: destruction still has not arrived after its own window.
    [InlineData(true, false, true, "ReturnState", 1.10, 1.00, 1.00, false)]
    // A hook still in FlyState gets no return grace (the FlyState deadline check owns that case).
    [InlineData(true, false, true, "FlyState", 5.00, 1.00, 1.00, false)]
    // Stuck hook released moments ago: Grip/Yank still has a full release window to reach ReturnState.
    [InlineData(true, true, false, "GripState", 5.00, 0.10, 1.00, true)]
    [InlineData(true, true, false, "GripState", 5.00, 1.10, 1.00, false)]
    // Still held: HookFallbackReason protects it; this helper must not claim a release grace.
    [InlineData(true, true, true, "GripState", 5.00, 0.10, 1.00, false)]
    // No hook object: the never-seen/gone branches do not need a return window.
    [InlineData(false, false, false, "ReturnState", 0.00, 0.00, 1.00, false)]
    public void ReturnFallbackGraceStartsAtTheReturnOrRelease(
        bool hookPresent,
        bool stuck,
        bool keyHeld,
        string hookStateName,
        double hookStateAgeSeconds,
        double releasedAgeSeconds,
        double fallbackTimeoutSeconds,
        bool expectedWithinGrace)
    {
        Assert.Equal(
            expectedWithinGrace,
            PredictionMath.ReturnFallbackWithinGrace(
                hookPresent, stuck, keyHeld, hookStateName,
                hookStateAgeSeconds, releasedAgeSeconds, fallbackTimeoutSeconds));
    }

    [Theory]
    [InlineData(10.0, 0.075, 0.2, 0.222, 1.0, 1.0, 0.15, 1.5)]  // raw 4.97, wide default cap
    [InlineData(10.0, 0.000, 0.2, 0.000, 1.0, 1.0, 0.15, 1.5)]  // 10 * 0.2 = 2.0 -> cap 1.5
    [InlineData(10.0, 0.000, 0.1, 0.000, 1.0, 1.0, 0.15, 1.0)]  // 10 * 0.1
    [InlineData(10.0, 0.000, 0.0, 0.050, 1.0, 0.5, 0.15, 0.25)] // 10 * 0.05 * 0.5
    [InlineData(10.0, 0.000, 0.0, 0.100, 2.0, 1.0, 0.15, 1.5)]  // strength 2 caps at 10 * 0.15
    [InlineData(10.0, 0.000, 0.0, 0.100, 1.0, 0.0, 0.15, 0.0)]  // no confidence, no lead
    [InlineData(0.0, 0.500, 0.2, 1.000, 1.0, 1.0, 0.15, 0.0)]   // stationary target
    public void AimLeadCappedScalesByStrengthConfidenceAndCap(
        double targetSpeed, double oneWaySeconds, double interpolationSeconds, double flightSeconds,
        double strength, double confidence, double capSeconds, double expectedMeters)
    {
        Assert.Equal(expectedMeters,
            PredictionMath.AimLeadCapped(
                targetSpeed, oneWaySeconds, interpolationSeconds, flightSeconds,
                strength, confidence, capSeconds), 12);
    }

    [Theory]
    [InlineData(5.0, 4.0, 0.90, 0.20, 0.30)] // ratio .8 -> .6 factor; dot .9 -> .5 factor
    [InlineData(5.0, 5.0, 1.00, 0.20, 1.00)]
    [InlineData(5.0, 5.0, 0.85, 0.20, 0.25)] // direction factor (0.85-0.8)/0.2
    [InlineData(1.4, 5.0, 1.00, 0.20, 0.00)] // below the 1.5 m/s floor
    [InlineData(5.0, 5.0, 1.00, 0.10, 0.00)] // shorter than three 0.1 s snapshots
    [InlineData(5.0, 5.0, 0.79, 0.20, 0.00)] // velocity turned too far
    [InlineData(5.0, 1.0, 1.00, 0.20, 0.00)] // speed ratio 0.2 below the floor
    [InlineData(7.5, 5.0, 1.00, 0.20, 0.3333333333333333)] // ratio 2/3 -> factor 1/3
    public void AimLeadConfidenceNeedsStableMultiFrameVelocity(
        double speed, double previousSpeed, double directionDot, double spanSeconds, double expected)
    {
        Assert.Equal(expected,
            PredictionMath.AimLeadConfidence(speed, previousSpeed, directionDot, spanSeconds), 12);
    }

    [Theory]
    [InlineData(5.0, 10.0, 0.35, 3.5)]
    [InlineData(-5.0, 10.0, 0.35, -3.5)]
    [InlineData(2.0, 10.0, 0.35, 2.0)]
    [InlineData(5.0, 0.0, 0.35, 0.0)]
    [InlineData(5.0, 10.0, 0.0, 0.0)]
    [InlineData(5.0, 10.0, 1.0, 5.0)]
    [InlineData(5.0, -10.0, 0.35, 3.5)] // magnitude contract; abs keeps it symmetric
    public void VerticalLeadIsLimitedRelativeToHorizontalSpeed(
        double verticalSpeed, double horizontalSpeed, double maxFraction, double expected)
    {
        Assert.Equal(expected,
            PredictionMath.ClampVerticalSpeed(verticalSpeed, horizontalSpeed, maxFraction), 12);
    }

    [Theory]
    [InlineData(20.0, 0.075, 1000.0, 1.500)]  // no wall: 20 * 0.075
    [InlineData(20.0, 0.075, 2.000, 1.500)]   // wall at lead + 0.5 m safety
    [InlineData(20.0, 0.075, 1.750, 1.250)]   // 1.75 - 0.5 = 1.25
    [InlineData(20.0, 0.075, 0.800, 0.300)]   // 0.8 - 0.5 = 0.3
    [InlineData(20.0, 0.075, 0.500, 0.000)]   // wall at safety margin -> no lead
    [InlineData(20.0, 0.075, 0.000, 0.000)]   // wallDistance 0 -> 0
    [InlineData(20.0, 1.000, 1000.0, 5.000)]  // StartLead cap still applies (20 * 0.25)
    public void StartLeadWithWallShortensLeadToWallClearance(
        double dashSpeed, double oneWaySeconds, double wallDistanceMeters, double expectedMeters)
    {
        Assert.Equal(expectedMeters,
            PredictionMath.StartLeadWithWall(dashSpeed, oneWaySeconds, wallDistanceMeters), 12);
    }

    [Theory]
    [InlineData(0.000, 0.150, 0.0)]   // start
    [InlineData(0.075, 0.150, 0.5)]   // halfway
    [InlineData(0.150, 0.150, 1.0)]   // end
    [InlineData(0.300, 0.150, 1.0)]   // beyond the end clamps
    [InlineData(-0.050, 0.150, 0.0)]  // before the start clamps
    [InlineData(0.050, 0.000, 1.0)]   // zero duration jumps to the end
    [InlineData(0.050, -0.100, 1.0)]  // negative duration jumps to the end
    public void InterpolationAlphaClampsProgressToUnitRange(
        double elapsedSeconds, double durationSeconds, double expectedAlpha)
    {
        Assert.Equal(expectedAlpha, PredictionMath.InterpolationAlpha(elapsedSeconds, durationSeconds), 12);
    }

    [Theory]
    [InlineData(0.020, 0.000, 0.250, 0.020)]  // first step: full frame
    [InlineData(0.020, 0.240, 0.250, 0.010)]  // only 10 ms left under the cap
    [InlineData(0.020, 0.250, 0.250, 0.000)]  // cap already reached
    [InlineData(0.020, 0.400, 0.250, 0.000)]  // past the cap stays at zero
    [InlineData(0.000, 0.100, 0.250, 0.000)]  // zero frame
    [InlineData(-0.020, 0.100, 0.250, 0.000)] // negative frame cannot rewind
    [InlineData(0.020, -0.100, 0.250, 0.020)] // negative travelled counts as zero
    [InlineData(0.020, 0.000, 0.000, 0.000)]  // zero cap: no movement
    [InlineData(0.020, 0.000, -0.250, 0.000)] // negative cap: no movement
    [InlineData(0.500, 0.000, 0.250, 0.250)]  // long frame is cut to the cap
    public void GhostStepSecondsClampsFrameToRemainingCap(
        double deltaSeconds, double traveledSeconds, double capSeconds, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds,
            PredictionMath.GhostStepSeconds(deltaSeconds, traveledSeconds, capSeconds), 12);
    }

    [Theory]
    [InlineData(5.0, 1000.0, 5.000)] // open space: full step
    [InlineData(5.0, 5.000, 4.500)]  // 5 - 0.5 safety margin
    [InlineData(5.0, 2.000, 1.500)]  // 2 - 0.5
    [InlineData(5.0, 0.500, 0.000)]  // wall at the safety margin: stop
    [InlineData(5.0, 0.000, 0.000)]  // wall at the origin: stop
    [InlineData(0.0, 1000.0, 0.000)] // zero step
    [InlineData(-2.0, 1000.0, 0.000)] // negative step
    [InlineData(0.3, 0.800, 0.300)]  // desired equals the clearance: unchanged
    [InlineData(5.0, double.PositiveInfinity, 5.000)] // no hit
    public void GhostStepWallLimitStopsBeforeWall(
        double desiredMeters, double wallDistanceMeters, double expectedMeters)
    {
        Assert.Equal(expectedMeters,
            PredictionMath.GhostStepWallLimit(desiredMeters, wallDistanceMeters), 12);
    }

    [Theory]
    [InlineData(0.000, false)]       // no handoff travel
    [InlineData(2.500, false)]       // normal dash drift
    [InlineData(30.000, false)]      // at the absolute limit: still blend
    [InlineData(30.001, true)]       // above the limit: teleport/respawn
    [InlineData(50.000, true)]       // far teleport
    public void HandoffIsTeleportAboveTheAbsoluteLimit(double distanceMeters, bool expected)
    {
        Assert.Equal(expected, PredictionMath.HandoffIsTeleport(distanceMeters, 0.0));
    }

    [Theory]
    // LoaderHook: maxTravelDistance 80 m / desiredForwardSpeed 360 m/s => base 0.2222 s.
    // ownExtra 0.1 s; the fixed 50 ms stuck-sync tick only applies while our FlyTimeout runs.
    [InlineData(80.0, 360.0, 0.1, true, true, false, false, 0.05, 2.225)]  // 0.4444 + 0.05 = 0.4944 s
    [InlineData(80.0, 360.0, 0.1, true, false, false, false, 0.05, 1.675)] // 0.3222 + 0.05 = 0.3722 s
    [InlineData(80.0, 360.0, 0.1, true, false, true, true, 0.05, 2.225)]   // Ruler fix on: same doubling, not stacked
    [InlineData(80.0, 360.0, 0.1, true, true, false, true, 0.05, 1.675)]   // Ruler present and explicitly off: no doubling
    [InlineData(80.0, 360.0, 0.1, false, true, false, false, 0.05, 1.0)]   // our FlyTimeout off, no Ruler: vanilla
    [InlineData(80.0, 360.0, 0.1, false, true, true, true, 0.05, 2.0)]     // our FlyTimeout off, Ruler still doubles
    [InlineData(80.0, 360.0, 0.1, true, true, false, false, 0.0, 2.0)]     // nominal deadline without the fixed tick
    [InlineData(80.0, 0.0, 0.1, true, true, false, false, 0.05, 1.0)]      // unknown speed: cannot derive, keep vanilla
    [InlineData(0.0, 360.0, 0.1, true, true, false, false, 0.05, 1.0)]     // zero travel: keep vanilla
    public void HookTimeoutMultiplierTracksTheConfiguredDeadline(
        double maxTravelDistanceMeters,
        double forwardSpeedMetersPerSecond,
        double ownExtraSeconds,
        bool flyTimeoutEnabled,
        bool provideRulerEquivalent,
        bool loaderRulerFixEnabled,
        bool loaderRulerPresent,
        double stuckGraceSeconds,
        double expectedMultiplier)
    {
        Assert.Equal(
            expectedMultiplier,
            PredictionMath.HookTimeoutMultiplier(
                maxTravelDistanceMeters, forwardSpeedMetersPerSecond, ownExtraSeconds,
                flyTimeoutEnabled, provideRulerEquivalent,
                loaderRulerFixEnabled, loaderRulerPresent, stuckGraceSeconds),
            12);
    }

    [Theory]
    [InlineData(80.0, 2.0, 0.25, 160.25)]  // 80 * 2 + 0.25
    [InlineData(80.0, 1.0, 0.25, 80.25)]   // vanilla
    [InlineData(80.0, 1.45, 0.25, 116.25)] // 80 * 1.45 = 116
    [InlineData(0.0, 2.0, 0.25, 0.25)]     // radius only
    [InlineData(-80.0, 2.0, 0.25, 0.25)]   // negative distance cannot go below the radius
    [InlineData(80.0, 0.5, 0.25, 80.25)]   // a multiplier below 1 never shrinks vanilla reach
    [InlineData(80.0, 2.0, -0.25, 160.0)]  // negative radius cannot subtract
    public void HookRangeScalesTravelByTimeoutMultiplier(
        double maxTravelDistanceMeters,
        double timeoutMultiplier,
        double colliderRadiusMeters,
        double expectedMeters)
    {
        Assert.Equal(
            expectedMeters,
            PredictionMath.HookRangeMeters(
                maxTravelDistanceMeters, timeoutMultiplier, colliderRadiusMeters),
            12);
    }

    [Fact]
    // Hand calculation for the acceptance example:
    // base = 80 / 360 = 0.22222 s.
    // default doubling: deadline = 0.22222 + max(0.22222, 0.10) = 0.44444 s.
    // plus the fixed 50 ms stuck-sync tick: 0.49444 s.
    // flight = 360 * 0.49444 = 178 m; reach = 178 + 0.25 = 178.25 m.
    public void DefaultLoaderHookReachIncludesTheFixedStuckSyncTick()
    {
        double multiplier = PredictionMath.HookTimeoutMultiplier(
            80.0, 360.0, 0.1, true, true, false, false, PredictionMath.StuckSyncTickSeconds);
        Assert.Equal(2.225, multiplier, 12);
        Assert.Equal(178.25, PredictionMath.HookRangeMeters(80.0, multiplier, 0.25), 12);
    }

    // ---- Enforcer local early end (P9) ----

    [Theory]
    // Trigger A = start + ghost motion: 10.00 + 0.45 = 10.45; RTT inputs are not used.
    [InlineData(10.0, 0.45, 0.30, 0.20, 10.45)]
    // Invalid motion (-0.1 -> 0) falls back to trigger B = start + oneWay + dash:
    // 10.00 + 0.30 + 0.20 = 10.50.
    [InlineData(10.0, -0.10, 0.30, 0.20, 10.50)]
    // Invalid motion, zero one-way: B = 10.00 + 0.00 + 0.20 = 10.20.
    [InlineData(10.0, 0.00, 0.00, 0.20, 10.20)]
    // Negative one-way cannot move B earlier: 10.00 + max(0, -0.5) + 0.40 = 10.40.
    [InlineData(10.0, 0.00, -0.50, 0.40, 10.40)]
    // Valid motion wins over the later RTT fallback (10.00 + 0.30 = 10.30, not 11.50).
    [InlineData(10.0, 0.30, 1.00, 0.50, 10.30)]
    public void LocalEndTriggerPrefersGhostMotionEndAndFallsBackToRtt(
        double startTime,
        double motionSeconds,
        double oneWaySeconds,
        double dashSeconds,
        double expectedTriggerTime)
    {
        Assert.Equal(
            expectedTriggerTime,
            PredictionMath.LocalEndTriggerTime(startTime, motionSeconds, oneWaySeconds, dashSeconds),
            12);
    }

    [Theory]
    // Limit = max(30, predicted + 2.5). predicted 10 -> 30; 9.21 <= 30 -> absorb.
    [InlineData(true, true, 9.21, 10.0, true)]
    // Boundary is inclusive (teleport test uses ">"): 30.00 <= 30 -> absorb.
    [InlineData(true, true, 30.00, 10.0, true)]
    // 30.01 > 30 -> let vanilla take over.
    [InlineData(true, true, 30.01, 10.0, false)]
    // predicted 40 -> limit max(30, 42.5) = 42.5; 42.0 <= 42.5 -> absorb.
    [InlineData(true, true, 42.00, 40.0, true)]
    // 43.0 > 42.5 -> vanilla.
    [InlineData(true, true, 43.00, 40.0, false)]
    // Local end never fired -> must use the vanilla path.
    [InlineData(false, true, 1.00, 10.0, false)]
    // Exit belongs to another body -> vanilla.
    [InlineData(true, false, 1.00, 10.0, false)]
    // Negative residual is bad data -> vanilla.
    [InlineData(true, true, -1.00, 10.0, false)]
    public void VanillaExitIsAbsorbedOnlyForOurFiredLocalEndWithinTeleportRange(
        bool localEndFired,
        bool bodyMatches,
        double residualMeters,
        double predictedTravelMeters,
        bool expectedAbsorb)
    {
        Assert.Equal(
            expectedAbsorb,
            PredictionMath.ShouldAbsorbVanillaExit(
                localEndFired, bodyMatches, residualMeters, predictedTravelMeters));
    }

    [Theory]
    // Latch = min(3.0, max(2.5, 3 * RTT)).
    [InlineData(0.000, 2.50)]  // 3 * 0 = 0 -> floor 2.50
    [InlineData(0.500, 2.50)]  // 3 * 0.5 = 1.5 -> floor 2.50
    [InlineData(0.800, 2.50)]  // 3 * 0.8 = 2.4 -> floor 2.50
    [InlineData(0.900, 2.70)]  // 3 * 0.9 = 2.70
    [InlineData(1.000, 3.00)]  // 3 * 1.0 = 3.00 at the cap
    [InlineData(2.000, 3.00)]  // 3 * 2.0 = 6.0 -> capped at 3.00
    [InlineData(-1.000, 2.50)] // negative RTT -> floor
    public void LocalEndLatchLivesTwoAndAHalfSecondsToThreeSeconds(
        double roundTripSeconds, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, PredictionMath.LocalEndLatchSeconds(roundTripSeconds), 12);
    }

    // ---- P10: RTT-spike aware stuck timeout (log line 419-432 case) ----

    [Theory]
    // Cached at entry: 3 * 0.352 = 1.056 s. At check time RTT = 0.966:
    // formula = min(max(3 * 0.966, 1), 3) = min(2.898, 3) = 2.898; max(1.056, 2.898) = 2.898.
    [InlineData(1.056, 0.966, 3.0, 2.898)]
    // RTT shrank (0.1 -> formula 1.0): the cached value must not shrink: max(1.056, 1.0) = 1.056.
    [InlineData(1.056, 0.100, 3.0, 1.056)]
    // RTT spike at the cap: min(max(6, 1), 3) = 3.0; max(1.2, 3.0) = 3.0.
    [InlineData(1.200, 2.000, 3.0, 3.0)]
    // Configured ceiling 1.5 still bounds the extension: min(3.0, 1.5) = 1.5; max(1.0, 1.5) = 1.5.
    [InlineData(1.000, 1.000, 1.5, 1.5)]
    // A cached value above the current formula is never lowered: max(4.0, 1.0) = 4.0.
    [InlineData(4.000, 0.100, 3.0, 4.0)]
    // Negative cache is unusable: max(0, min(max(0.3, 1), 3)) = 1.0.
    [InlineData(-1.000, 0.100, 3.0, 1.0)]
    // NaN cache is unusable: same as 0.
    [InlineData(double.NaN, 0.100, 3.0, 1.0)]
    public void ExtendStuckTimeoutGrowsWithAnRttSpikeButNeverShrinks(
        double cachedSeconds, double roundTripSeconds, double maxSeconds, double expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            PredictionMath.ExtendStuckTimeout(cachedSeconds, roundTripSeconds, maxSeconds),
            12);
    }

    // ---- P10: exit-smoothing hard-snap guard (log lines 263 / 330 / 347) ----

    [Theory]
    // Real log values. With a ghost, the teleport limit is max(30, predicted + 2.5).
    // 16.30 m vs predicted 16.48: limit max(30, 18.98) = 30 -> false (drift, smooth).
    [InlineData(16.30, 16.48, false)]
    // 13.23 m vs predicted 15.53: limit 30 -> false.
    [InlineData(13.23, 15.53, false)]
    // 67.75 m vs predicted 13.70: limit 30 -> true (teleport/respawn, hard snap).
    [InlineData(67.75, 13.70, true)]
    // 9.21 m was already smoothed before; still false.
    [InlineData(9.21, 16.59, false)]
    // Prediction outgrows the fixed 30 m base: limit max(30, 42.5) = 42.5; 42.0 -> false.
    [InlineData(42.00, 40.00, false)]
    // 43.0 > 42.5 -> true.
    [InlineData(43.00, 40.00, true)]
    // No usable prediction (ghost off/not armed): keep the old fixed 10 m threshold.
    [InlineData(9.21, 0.00, false)]
    [InlineData(16.30, 0.00, true)]
    // Negative and NaN predictions are unusable and take the 10 m fallback.
    [InlineData(9.21, -1.00, false)]
    [InlineData(67.75, double.NaN, true)]
    public void ExitSmoothingIsTeleportUsesPredictedDashTravel(
        double distanceMeters, double predictedTravelMeters, bool expected)
    {
        Assert.Equal(
            expected,
            PredictionMath.ExitSmoothingIsTeleport(distanceMeters, predictedTravelMeters, 10.0));
    }
}
