using System;

namespace RoR2LatencyMod;

/// <summary>Why a leftover client-only predicted hook is removed (visual prediction cleanup).</summary>
public enum PredictedHookDropReason
{
    None,
    Handoff,
    FireHookExit,
    Timeout,
}

/// <summary>
/// Shared prediction/interpolation math. Pure numbers only: no Unity or game types.
/// </summary>
public static class PredictionMath
{
    /// <summary>Fallback interpolation delay when the target has no CharacterNetworkTransform (P2).</summary>
    public const double DefaultInterpolationSeconds = 0.2;

    /// <summary>Start-lead is capped at the distance travelled in this many seconds (P4).</summary>
    public const double StartLeadCapSeconds = 0.25;

    /// <summary>Below this snapshot speed a target counts as stationary for aim lead (m/s).</summary>
    public const double MinAimLeadSpeedMetersPerSecond = 1.5;

    /// <summary>Least snapshot span for a stable velocity: three 0.1 s CharacterNetworkTransform samples.</summary>
    public const double MinAimLeadSpanSeconds = 0.12;

    /// <summary>Two consecutive velocity estimates must agree at least this much (dot).</summary>
    public const double MinAimLeadDirectionDot = 0.8;

    /// <summary>Speed ratio floor between two consecutive velocity estimates.</summary>
    public const double MinAimLeadSpeedRatio = 0.5;

    /// <summary>Vertical lead is limited to this fraction of the horizontal speed (flying enemies bob).</summary>
    public const double AimLeadVerticalFraction = 0.35;

    /// <summary>Fallback timeout floor (P5). 0.3 s fired before 1x-1.5x RTT arrivals in the real room.</summary>
    public const double MinStuckTimeoutSeconds = 1.0;

    /// <summary>Lowest configured ceiling accepted (Enforcer/FeedbackFallbackMaxSeconds range starts at 0.5).</summary>
    public const double MinStuckTimeoutCapSeconds = 0.5;

    /// <summary>Stuck-timeout ceiling (P5); a real RTT spike can otherwise park the dash pose for seconds.</summary>
    public const double MaxStuckTimeoutSeconds = 3.0;

    /// <summary>Interpolation duration range (P6).</summary>
    public const double MinInterpolationSeconds = 0.1;
    public const double MaxInterpolationSeconds = 0.2;

    /// <summary>P8: hard ceiling for the locally added fly-timeout extra (seconds).</summary>
    public const double MaxFlyTimeoutExtraSeconds = 4.0;

    /// <summary>Upper bound for the owner-side buff multipliers (hook return speed, Enforcer dash length).</summary>
    public const double MaxBuffMultiplier = 10.0;

    /// <summary>Buff multiplier, clamped to [1, 10]; non-finite means vanilla.</summary>
    public static double BuffMultiplier(double requested) =>
        double.IsNaN(requested) || double.IsInfinity(requested)
            ? 1.0
            : Math.Min(Math.Max(1.0, requested), MaxBuffMultiplier);

    /// <summary>P4: wall clearance kept when shortening the start lead (metres).</summary>
    public const double WallSafetyMarginMeters = 0.5;

    /// <summary>P2: speed * (U + interpolation delay + projectile flight time).</summary>
    public static double AimLead(
        double targetSpeed, double oneWayLatencySeconds, double interpolationSeconds, double flightSeconds)
        => targetSpeed * (oneWayLatencySeconds + interpolationSeconds + flightSeconds);

    /// <summary>P4: speed * U, capped at StartLeadCapSeconds of dash travel.</summary>
    public static double StartLead(double dashSpeed, double oneWayLatencySeconds)
        => dashSpeed * Math.Min(Math.Max(0.0, oneWayLatencySeconds), StartLeadCapSeconds);

    /// <summary>P5: max(3 * RTT, 1 s), capped (default 3 s). A 3x RTT margin covers jittery arrivals.</summary>
    public static double StuckTimeout(double roundTripSeconds)
        => StuckTimeout(roundTripSeconds, MaxStuckTimeoutSeconds);

    /// <summary>P5: same, with a configured ceiling for the real-room tuning knob.</summary>
    public static double StuckTimeout(double roundTripSeconds, double maxSeconds)
        => Math.Min(
            Math.Max(3.0 * Math.Max(0.0, roundTripSeconds), MinStuckTimeoutSeconds),
            SanitizeCap(maxSeconds));

    /// <summary>P7: ghost hold limit = max(2 * RTT, 0.5 s), capped like the fallback.</summary>
    public static double HoldTimeout(double roundTripSeconds, double maxSeconds)
        => Math.Min(
            Math.Max(2.0 * Math.Max(0.0, roundTripSeconds), 0.5),
            SanitizeCap(maxSeconds));

    private static double SanitizeCap(double maxSeconds)
        => double.IsNaN(maxSeconds) || double.IsInfinity(maxSeconds)
            ? MaxStuckTimeoutSeconds
            : Math.Min(Math.Max(maxSeconds, MinStuckTimeoutCapSeconds), 60.0);

    /// <summary>
    /// P10: a timeout cached at dash entry must grow when the RTT spikes mid-dash (the log's
    /// 0.352 s -> 0.966 s spike fired the fallback 0.35 s before the state arrived), but a later
    /// smaller sample must never shrink it. The configured ceiling still applies.
    /// </summary>
    public static double ExtendStuckTimeout(
        double cachedSeconds, double roundTripSeconds, double maxSeconds)
        => Math.Max(FiniteOrZero(cachedSeconds), StuckTimeout(roundTripSeconds, maxSeconds));

    /// <summary>
    /// Visual-only hook prediction cleanup: the client predicted copy must be dropped on the
    /// authority handoff, when the firing skill state exits, or after max(2 x RTT, 0.5 s) capped
    /// like the fallback. An instance already destroyed is never touched again: the handoff moved
    /// its ghost to the authority projectile first, so destroying it twice would kill that ghost.
    /// </summary>
    public static PredictedHookDropReason PredictedHookDrop(
        bool handedOff,
        bool fireHookExited,
        bool alreadyDestroyed,
        double ageSeconds,
        double roundTripSeconds,
        double maxSeconds)
    {
        if (alreadyDestroyed)
        {
            return PredictedHookDropReason.None;
        }

        if (handedOff)
        {
            return PredictedHookDropReason.Handoff;
        }

        if (fireHookExited)
        {
            return PredictedHookDropReason.FireHookExit;
        }

        return FiniteOrZero(ageSeconds) >= HoldTimeout(roundTripSeconds, maxSeconds)
            ? PredictedHookDropReason.Timeout
            : PredictedHookDropReason.None;
    }

    /// <summary>P6: clamp the exit-position interpolation duration to 0.1-0.2 s.</summary>
    public static double ClampInterpolationDuration(double requestedSeconds)
        => Math.Min(Math.Max(requestedSeconds, MinInterpolationSeconds), MaxInterpolationSeconds);

    /// <summary>Slack for the server/client fixed-step boundary when a stuck SyncVar is in flight.</summary>
    public const double StuckSyncTickSeconds = 0.05;

    /// <summary>Ceiling for the stuck-sync grace so an extreme RTT cannot park a missed hook.</summary>
    public const double MaxStuckSyncGraceSeconds = 1.0;

    /// <summary>
    /// P8: final FlyState deadline. <paramref name="provideRulerEquivalent"/> (default on) makes the
    /// client wait base + max(base, ownExtra), the same expiry LoaderRuler's "Fix Grapple Range"
    /// produces by adding dt/2 every tick (about 2 * base), with or without the mod installed.
    /// When LoaderRuler is present and its fix is on, its runtime already doubles, so the same
    /// formula applies and no second extra is stacked. When LoaderRuler is present but its fix is
    /// explicitly off, the user opted out of the doubling: keep base + ownExtra (#6). Without
    /// LoaderRuler, <paramref name="provideRulerEquivalent"/> alone decides.
    /// </summary>
    public static double FlyDeadlineSeconds(
        double baseFlySeconds,
        double ownExtraSeconds,
        bool provideRulerEquivalent,
        bool loaderRulerFixEnabled,
        bool loaderRulerPresent)
    {
        double baseSeconds = FiniteOrZero(baseFlySeconds);
        double extra = Math.Min(Math.Max(0.0, FiniteOrZero(ownExtraSeconds)), MaxFlyTimeoutExtraSeconds);
        bool doubleTimeout = loaderRulerPresent ? loaderRulerFixEnabled : provideRulerEquivalent;
        if (doubleTimeout)
        {
            extra = Math.Max(baseSeconds, extra);
        }

        return baseSeconds + extra;
    }

    /// <summary>
    /// #2: how much longer than vanilla the hook's nominal flight lasts. The FlyState deadline is
    /// base + max(base, ownExtra) while our FlyTimeout patch runs; the fixed stuck-sync tick
    /// delays the forced return, so the crosshair includes it (U itself is left out to avoid
    /// drifting with network jitter). LoaderRuler's own doubling still applies when our patch is
    /// off. A non-positive distance or speed cannot produce a duration, so fall back to 1x.
    /// </summary>
    public static double HookTimeoutMultiplier(
        double maxTravelDistanceMeters,
        double forwardSpeedMetersPerSecond,
        double ownExtraSeconds,
        bool flyTimeoutEnabled,
        bool provideRulerEquivalent,
        bool loaderRulerFixEnabled,
        bool loaderRulerPresent,
        double stuckGraceSeconds)
    {
        double distance = Math.Max(0.0, FiniteOrZero(maxTravelDistanceMeters));
        double speed = FiniteOrZero(forwardSpeedMetersPerSecond);
        if (distance <= 0.0 || speed <= 0.0)
        {
            return 1.0;
        }

        double baseSeconds = distance / speed;
        if (!flyTimeoutEnabled)
        {
            return loaderRulerPresent && loaderRulerFixEnabled ? 2.0 : 1.0;
        }

        return (FlyDeadlineSeconds(
                    baseSeconds, ownExtraSeconds, provideRulerEquivalent,
                    loaderRulerFixEnabled, loaderRulerPresent)
                + Math.Max(0.0, FiniteOrZero(stuckGraceSeconds)))
               / baseSeconds;
    }

    /// <summary>
    /// #2: crosshair reach = nominal flight distance at the configured timeout + the hook's own
    /// collider radius. A multiplier below 1 would understate vanilla, so clamp it at 1.
    /// </summary>
    public static double HookRangeMeters(
        double maxTravelDistanceMeters,
        double timeoutMultiplier,
        double colliderRadiusMeters)
        => Math.Max(0.0, FiniteOrZero(maxTravelDistanceMeters))
           * Math.Max(1.0, FiniteOrZero(timeoutMultiplier))
           + Math.Max(0.0, FiniteOrZero(colliderRadiusMeters));

    /// <summary>
    /// #3: STUCK IS A SERVER SYNCVAR (ProjectileStickOnImpact.cs:63-64,96). The client hook has no
    /// local collision, so a genuine hit can be learned as late as one-way latency plus one
    /// fixed-step boundary after the server's own deadline. Wait that long before forcing
    /// ReturnState; the 50 ms slack covers server send plus client fixed-update alignment. The
    /// 1 s cap keeps a missed hook from parking when the RTT estimate is extreme.
    /// </summary>
    public static double StuckSyncGraceSeconds(double oneWayLatencySeconds)
        => Math.Min(
            Math.Max(0.0, FiniteOrZero(oneWayLatencySeconds)) + StuckSyncTickSeconds,
            MaxStuckSyncGraceSeconds);

    /// <summary>#3: FlyState deadline including the wait for a server hit SyncVar to arrive.</summary>
    public static double FlyDeadlineWithStuckGrace(
        double baseFlySeconds,
        double ownExtraSeconds,
        bool provideRulerEquivalent,
        bool loaderRulerFixEnabled,
        bool loaderRulerPresent,
        double oneWayLatencySeconds)
        => FlyDeadlineSeconds(
               baseFlySeconds, ownExtraSeconds, provideRulerEquivalent,
               loaderRulerFixEnabled, loaderRulerPresent)
           + StuckSyncGraceSeconds(oneWayLatencySeconds);

    /// <summary>
    /// #3: why the owner-side FireHook fallback may return to Main, or null while it must wait.
    /// A synced hit is never overridden while the key is held, and a hook still inside its own
    /// latency-graced FlyState deadline is protected from an early main-state exit.
    /// </summary>
    public static string HookFallbackReason(
        bool hookPresent,
        bool sawHook,
        bool hookGone,
        bool stuck,
        bool keyHeld,
        string hookStateName,
        bool flyWithinDeadline)
    {
        if (hookPresent)
        {
            if (hookStateName == "ReturnState")
            {
                return "ReturnState not destroyed";
            }

            if (!stuck)
            {
                return flyWithinDeadline ? null : "not stuck and not returning";
            }

            return keyHeld ? null : "released while stuck, no return";
        }

        if (!sawHook)
        {
            return "never got hookInstance";
        }

        return hookGone ? "hook destroyed but FireHook did not exit" : null;
    }

    /// <summary>
    /// #3: true while a healthy return still deserves its own window. The timer starts when the
    /// hook enters ReturnState (or when the key is released while stuck), not when FireHook
    /// started: a long hold must not make a freshly returning hook look overdue. The fallback
    /// timeout is at least max(3 x RTT, 1 s), which already covers the return flight plus the
    /// server destroy round trip.
    /// </summary>
    public static bool ReturnFallbackWithinGrace(
        bool hookPresent,
        bool stuck,
        bool keyHeld,
        string hookStateName,
        double hookStateAgeSeconds,
        double releasedAgeSeconds,
        double fallbackTimeoutSeconds)
    {
        if (!hookPresent)
        {
            return false;
        }

        double timeout = Math.Max(0.0, FiniteOrZero(fallbackTimeoutSeconds));
        if (hookStateName == "ReturnState")
        {
            return FiniteOrZero(hookStateAgeSeconds) < timeout;
        }

        return stuck && !keyHeld && FiniteOrZero(releasedAgeSeconds) < timeout;
    }

    /// <summary>
    /// P2 conservative lead: 0 unless the last two snapshot velocities agree, the sample spans
    /// several network ticks and the target clearly moves. Scales 0-1 with agreement.
    /// </summary>
    public static double AimLeadConfidence(
        double speed,
        double previousSpeed,
        double directionDot,
        double sampleSpanSeconds)
    {
        if (!IsFiniteNumber(speed) || !IsFiniteNumber(previousSpeed) || !IsFiniteNumber(directionDot) ||
            !IsFiniteNumber(sampleSpanSeconds) ||
            speed < MinAimLeadSpeedMetersPerSecond ||
            previousSpeed <= 0.0 ||
            sampleSpanSeconds < MinAimLeadSpanSeconds ||
            directionDot <= MinAimLeadDirectionDot)
        {
            return 0.0;
        }

        double speedRatio = Math.Min(speed, previousSpeed) / Math.Max(speed, previousSpeed);
        double speedFactor = Clamp01((speedRatio - MinAimLeadSpeedRatio) / (1.0 - MinAimLeadSpeedRatio));
        double directionFactor = Clamp01((directionDot - MinAimLeadDirectionDot) / (1.0 - MinAimLeadDirectionDot));
        return Clamp01(speedFactor * directionFactor);
    }

    /// <summary>Keep a flying target's vertical bobbing from dominating the lead vector.</summary>
    public static double ClampVerticalSpeed(double verticalSpeed, double horizontalSpeed, double maxFraction)
        => Math.Min(
            Math.Max(FiniteOrZero(verticalSpeed), -Math.Abs(FiniteOrZero(horizontalSpeed)) * Clamp01(FiniteOrZero(maxFraction))),
            Math.Abs(FiniteOrZero(horizontalSpeed)) * Clamp01(FiniteOrZero(maxFraction)));

    /// <summary>P2: AimLead scaled by strength and confidence, capped at capSeconds of target travel.</summary>
    public static double AimLeadCapped(
        double targetSpeed,
        double oneWayLatencySeconds,
        double interpolationSeconds,
        double flightSeconds,
        double strength,
        double confidence,
        double capSeconds)
    {
        double speed = Math.Max(0.0, targetSpeed);
        double lead = speed
                      * (Math.Max(0.0, oneWayLatencySeconds) + Math.Max(0.0, interpolationSeconds) +
                         Math.Max(0.0, flightSeconds))
                      * Math.Max(0.0, strength)
                      * Clamp01(confidence);
        return Math.Min(lead, speed * Math.Max(0.0, capSeconds));
    }

    private static double FiniteOrZero(double value)
        => IsFiniteNumber(value) ? value : 0.0;

    private static bool IsFiniteNumber(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Clamp01(double value)
        => Math.Min(Math.Max(FiniteOrZero(value), 0.0), 1.0);

    /// <summary>P4: StartLead shortened so the dash starts at least 0.5 m away from a wall.</summary>
    public static double StartLeadWithWall(
        double dashSpeed,
        double oneWayLatencySeconds,
        double wallDistanceMeters)
    {
        double wallLimit = Math.Max(0.0, wallDistanceMeters - WallSafetyMarginMeters);
        return Math.Max(0.0, Math.Min(StartLead(dashSpeed, oneWayLatencySeconds), wallLimit));
    }

    /// <summary>P6: normalized 0-1 smoothing progress; non-positive duration is treated as finished.</summary>
    public static double InterpolationAlpha(double elapsedSeconds, double durationSeconds)
        => durationSeconds <= 0.0
            ? 1.0
            : Math.Min(Math.Max(elapsedSeconds / durationSeconds, 0.0), 1.0);

    /// <summary>P7: seconds advanced by one ghost step, never past the remaining dash cap.</summary>
    public static double GhostStepSeconds(double deltaSeconds, double traveledSeconds, double capSeconds)
    {
        double remaining = Math.Max(0.0, capSeconds) - Math.Max(0.0, traveledSeconds);
        return Math.Max(0.0, Math.Min(Math.Max(0.0, deltaSeconds), Math.Max(0.0, remaining)));
    }

    /// <summary>P7: ghost step shortened so it stops WallSafetyMarginMeters before a wall.</summary>
    public static double GhostStepWallLimit(double desiredMeters, double wallDistanceMeters)
    {
        double wallLimit = Math.Max(0.0, wallDistanceMeters - WallSafetyMarginMeters);
        return Math.Max(0.0, Math.Min(Math.Max(0.0, desiredMeters), wallLimit));
    }

    /// <summary>P7: absolute teleport guard; base dash (0.25 s lead + 0.2 s dash) at 50 m/s is 22.5 m.</summary>
    public const double MaxHandoffDistanceMeters = 30.0;

    /// <summary>P7: slack added to the ghost's own predicted travel before a gap counts as a teleport.</summary>
    public const double HandoffMarginMeters = 2.5;

    /// <summary>P7: the seat may trail the ghost by this much (client interpolation lag) and still be caught up.</summary>
    public const double HandoffCatchUpToleranceMeters = 2.0;

    /// <summary>P7: a seat this far off the dash line is not the same dash; do not blend.</summary>
    public const double MaxHandoffLateralMeters = 2.5;

    /// <summary>P7: the vehicle has reached or passed the ghost along the dash direction.</summary>
    public static bool HandoffCaughtUp(double alongMeters, double lateralMeters)
        => alongMeters >= -HandoffCatchUpToleranceMeters && lateralMeters <= MaxHandoffLateralMeters;

    /// <summary>P7: teleport allowance grows with the ghost's own predicted travel (fast items).</summary>
    public static double HandoffDistanceLimit(double predictedTravelMeters)
        => Math.Max(
            MaxHandoffDistanceMeters,
            Math.Max(0.0, predictedTravelMeters) + HandoffMarginMeters);

    /// <summary>P7: true when the ghost/vehicle gap is too large to blend; use the vanilla snap.</summary>
    public static bool HandoffIsTeleport(double distanceMeters, double predictedTravelMeters)
        => distanceMeters > HandoffDistanceLimit(predictedTravelMeters);

    /// <summary>
    /// P10: exit smoothing's teleport guard. The fixed fallbackLimitMeters (10 m) is smaller
    /// than the drift of a fast dash (16.30 / 13.23 m in the log), so use the last dash's
    /// predicted travel once the ghost supplied one; without a prediction keep the old limit.
    /// </summary>
    public static bool ExitSmoothingIsTeleport(
        double distanceMeters, double predictedTravelMeters, double fallbackLimitMeters)
        => IsFiniteNumber(predictedTravelMeters) && predictedTravelMeters > 0.0
            ? HandoffIsTeleport(distanceMeters, predictedTravelMeters)
            : distanceMeters > fallbackLimitMeters;

    /// <summary>P9: floor for the local-end latch; covers the latest seat/exit arrivals seen in logs.</summary>
    public const double MinLocalEndLatchSeconds = 2.5;

    /// <summary>
    /// P9 trigger A: the ghost's predicted motion end. If the motion length is unusable
    /// (no curve/duration), fall back to the RTT-based server exit estimate (trigger B).
    /// </summary>
    public static double LocalEndTriggerTime(
        double startTime, double motionSeconds, double oneWaySeconds, double dashSeconds)
    {
        double motion = FiniteOrZero(motionSeconds);
        if (motion > 0.0)
        {
            return startTime + motion;
        }

        return startTime
               + Math.Max(0.0, FiniteOrZero(oneWaySeconds))
               + Math.Max(0.0, FiniteOrZero(dashSeconds));
    }

    /// <summary>
    /// P9 eject merge: a fired local end owns the vanilla exit for the same body unless the
    /// exit position is a teleport/respawn (same distance rule as the ghost handoff).
    /// </summary>
    public static bool ShouldAbsorbVanillaExit(
        bool localEndFired, bool bodyMatches, double residualMeters, double predictedTravelMeters)
    {
        if (!localEndFired || !bodyMatches || !IsFiniteNumber(residualMeters) || residualMeters < 0.0)
        {
            return false;
        }

        return !HandoffIsTeleport(residualMeters, predictedTravelMeters);
    }

    /// <summary>P9 latch lifetime: max(3 x RTT, 2.5 s), capped like the stuck fallback.</summary>
    public static double LocalEndLatchSeconds(double roundTripSeconds)
        => Math.Min(
            MaxStuckTimeoutSeconds,
            Math.Max(MinLocalEndLatchSeconds, 3.0 * Math.Max(0.0, FiniteOrZero(roundTripSeconds))));
}
