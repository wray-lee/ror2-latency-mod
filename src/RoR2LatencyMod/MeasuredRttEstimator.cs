using System;
using System.Collections.Generic;

namespace RoR2LatencyMod;

/// <summary>
/// Locally measured round trip: Proxy dash enter -> vehicle state/seat arrival.
/// Pending enters are matched to arrivals FIFO, so repeated presses cannot reorder samples.
/// The median of a bounded window resists one late/early outlier; hard bounds reject
/// samples that cannot be a round trip. Pure numbers only: no Unity or game types.
/// </summary>
public sealed class MeasuredRttEstimator
{
    /// <summary>Below a plausible LAN round trip; a paired arrival would produce a fake sample.</summary>
    public const double MinSampleSeconds = 0.05;

    /// <summary>Beyond this the matching press is lost rather than a useful sample.</summary>
    public const double MaxSampleSeconds = 8.0;

    /// <summary>Recent samples kept for the median; older latency is no longer relevant.</summary>
    public const int SampleWindow = 5;

    /// <summary>Unmatched presses kept; deeper queues only accumulate lost samples.</summary>
    public const int MaxPending = 8;

    private readonly List<double> pendingEnterSeconds = new List<double>();
    private readonly List<double> samples = new List<double>();

    public int SampleCount => samples.Count;

    public int PendingCount => pendingEnterSeconds.Count;

    /// <summary>Records one local dash enter; the next arrival consumes it.</summary>
    public void ObserveEnter(double nowSeconds)
    {
        if (!IsFinite(nowSeconds))
        {
            return;
        }

        PrunePending(nowSeconds);
        pendingEnterSeconds.Add(nowSeconds);
        while (pendingEnterSeconds.Count > MaxPending)
        {
            pendingEnterSeconds.RemoveAt(0);
        }
    }

    /// <summary>
    /// Matches the oldest pending enter to this arrival. Returns false when there is no
    /// pending press or the gap cannot be a round trip (the pending entry is still dropped).
    /// </summary>
    public bool TryConsumeArrival(double nowSeconds, out double sampleSeconds)
    {
        sampleSeconds = 0.0;
        if (!IsFinite(nowSeconds))
        {
            return false;
        }

        PrunePending(nowSeconds);
        if (pendingEnterSeconds.Count == 0)
        {
            return false;
        }

        var entered = pendingEnterSeconds[0];
        pendingEnterSeconds.RemoveAt(0);
        var sample = nowSeconds - entered;
        if (sample < MinSampleSeconds || sample > MaxSampleSeconds)
        {
            return false;
        }

        samples.Add(sample);
        if (samples.Count > SampleWindow)
        {
            samples.RemoveAt(0);
        }

        sampleSeconds = sample;
        return true;
    }

    /// <summary>Median of the current window; false until the first accepted sample.</summary>
    public bool TryGetMedian(out double medianSeconds)
    {
        var count = samples.Count;
        if (count == 0)
        {
            medianSeconds = 0.0;
            return false;
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);
        medianSeconds = (count & 1) == 1
            ? sorted[count / 2]
            : 0.5 * (sorted[count / 2 - 1] + sorted[count / 2]);
        return true;
    }

    public void Reset()
    {
        pendingEnterSeconds.Clear();
        samples.Clear();
    }

    private void PrunePending(double nowSeconds)
    {
        // A press whose arrival is already older than MaxSampleSeconds can never produce
        // a valid sample; also drop entries from before a clock reset.
        pendingEnterSeconds.RemoveAll(entered => nowSeconds < entered || nowSeconds - entered > MaxSampleSeconds);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
