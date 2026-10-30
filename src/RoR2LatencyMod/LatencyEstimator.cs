using System;

namespace RoR2LatencyMod;

/// <summary>
/// One-way latency U = effective RTT / 2. The effective RTT is the lower of the game's
/// client-side smoothed RTT and the locally measured enter->arrival RTT (when samples exist);
/// without samples the game value is capped so an inflated reading cannot park a skill for
/// seconds. As Host there is nothing to predict, so no estimate is produced.
/// </summary>
public sealed class LatencyEstimator
{
    private readonly Func<double> readSmoothedRttSeconds;
    private readonly Func<double> nowSeconds;
    private readonly Func<bool> isHost;
    private readonly Func<double> initialRttCapSeconds;

    public LatencyEstimator(
        Func<double> readSmoothedRttSeconds,
        Func<double> nowSeconds,
        Func<bool> isHost,
        MeasuredRttEstimator measured = null,
        Func<double> initialRttCapSeconds = null)
    {
        this.readSmoothedRttSeconds = readSmoothedRttSeconds;
        this.nowSeconds = nowSeconds;
        this.isHost = isHost;
        this.initialRttCapSeconds = initialRttCapSeconds;
        Measured = measured;
    }

    /// <summary>Local enter->arrival samples, fed by the Enforcer patches; null elsewhere.</summary>
    public MeasuredRttEstimator Measured { get; }

    /// <summary>Injected-clock time of the last client-side sample, for diagnostics.</summary>
    public double LastSampleTimeSeconds { get; private set; }

    /// <summary>Last value the game reported, before selection; for the paired log line.</summary>
    public double LastGameReportedRttSeconds { get; private set; }

    /// <summary>Last measured median in seconds; NaN until the first accepted sample.</summary>
    public double LastMeasuredRttSeconds { get; private set; } = double.NaN;

    /// <summary>True when the effective RTT came from local samples rather than the game.</summary>
    public bool LastUsedMeasured { get; private set; }

    /// <summary>False on the Host, where latency prediction is disabled.</summary>
    public bool TryGetOneWayLatency(out double oneWaySeconds)
    {
        if (isHost())
        {
            oneWaySeconds = 0.0;
            return false;
        }

        // Network-derived sample: NaN/Inf must not reach the position/timeout math
        // (NaN start-lead would write a NaN position; NaN fly-timeout would never expire).
        double gameRtt = SanitizeRtt(readSmoothedRttSeconds());
        double measuredRtt = 0.0;
        var hasMeasured = Measured != null && Measured.TryGetMedian(out measuredRtt);

        double effectiveRtt = 0.0;
        if (hasMeasured)
        {
            // gameRtt <= 0 means the game has no reading yet, not zero latency.
            LastUsedMeasured = gameRtt <= 0.0 || measuredRtt <= gameRtt;
            effectiveRtt = gameRtt > 0.0 ? Math.Min(gameRtt, measuredRtt) : measuredRtt;
        }
        else
        {
            LastUsedMeasured = false;
            effectiveRtt = Math.Min(gameRtt, ReadInitialRttCap());
        }

        LastGameReportedRttSeconds = gameRtt;
        LastMeasuredRttSeconds = hasMeasured ? measuredRtt : double.NaN;
        oneWaySeconds = Math.Max(0.0, effectiveRtt) / 2.0;
        LastSampleTimeSeconds = nowSeconds();
        return true;
    }

    /// <summary>
    /// RTT used for fallback deadlines: the larger of the game reading and the measured
    /// median, so a latency spike does not fire the fallback before a late arrival.
    /// PredictionMath clamps the resulting timeout to the configured ceiling.
    /// </summary>
    public bool TryGetTimeoutRoundTrip(out double roundTripSeconds)
    {
        if (isHost())
        {
            roundTripSeconds = 0.0;
            return false;
        }

        double gameRtt = SanitizeRtt(readSmoothedRttSeconds());
        double measuredRtt = 0.0;
        bool hasMeasured = Measured != null && Measured.TryGetMedian(out measuredRtt);
        roundTripSeconds = Math.Max(gameRtt, hasMeasured ? measuredRtt : 0.0);
        return roundTripSeconds > 0.0;
    }

    /// <summary>Both values plus the sample count, for one log line per event.</summary>
    public string Describe()
    {
        var measured = double.IsNaN(LastMeasuredRttSeconds)
            ? "none"
            : $"{LastMeasuredRttSeconds:F3}s";
        return $"gameRTT={LastGameReportedRttSeconds:F3}s measuredRTT={measured} " +
               $"samples={Measured?.SampleCount ?? 0} used={(LastUsedMeasured ? "measured" : "game")}";
    }

    private double ReadInitialRttCap()
    {
        if (initialRttCapSeconds == null)
        {
            return double.PositiveInfinity;
        }

        var cap = initialRttCapSeconds();
        return double.IsNaN(cap) || double.IsInfinity(cap) || cap <= 0.0
            ? double.PositiveInfinity
            : cap;
    }

    private static double SanitizeRtt(double rttSeconds)
        => double.IsNaN(rttSeconds) || double.IsInfinity(rttSeconds) ? 0.0 : Math.Max(0.0, rttSeconds);
}
