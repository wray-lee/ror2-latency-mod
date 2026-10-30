using System;
using Xunit;

namespace RoR2LatencyMod.Tests;

public class LatencyEstimatorTests
{
    private static LatencyEstimator Create(double rttSeconds, bool isHost = false, double now = 0)
        => new LatencyEstimator(() => rttSeconds, () => now, () => isHost);

    [Theory]
    [InlineData(0.0, 0.0)]       // 0 ms RTT
    [InlineData(0.150, 0.075)]   // 150 ms RTT -> U = 75 ms
    [InlineData(0.500, 0.250)]   // 500 ms RTT -> U = 250 ms
    public void OneWayLatencyIsHalfTheSmoothedRtt(double rttSeconds, double expectedOneWaySeconds)
    {
        var estimator = Create(rttSeconds);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(expectedOneWaySeconds, oneWaySeconds, 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteRttIsTreatedAsZero(double rttSeconds)
    {
        var estimator = Create(rttSeconds);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(0.0, oneWaySeconds);
    }

    [Fact]
    public void HostHasNoLatencyEstimate()
    {
        var rttWasRead = false;
        var estimator = new LatencyEstimator(
            () =>
            {
                rttWasRead = true;
                return 0.500;
            },
            () => 0.0,
            () => true);

        Assert.False(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(0.0, oneWaySeconds);
        Assert.False(rttWasRead);
    }

    [Fact]
    public void SampleIsStampedWithInjectedTimeSource()
    {
        var estimator = Create(0.150, now: 42.5);

        Assert.True(estimator.TryGetOneWayLatency(out _));
        Assert.Equal(42.5, estimator.LastSampleTimeSeconds, 12);
    }

    private static MeasuredRttEstimator MeasuredWith(double sampleSeconds)
    {
        var measured = new MeasuredRttEstimator();
        measured.ObserveEnter(0.0);
        Assert.True(measured.TryConsumeArrival(sampleSeconds, out _));
        return measured;
    }

    [Fact]
    public void MeasuredRttWinsWhenItIsLowerThanTheGameReportedRtt()
    {
        var measured = MeasuredWith(2.0);
        var estimator = new LatencyEstimator(() => 10.0, () => 0.0, () => false, measured);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(1.0, oneWaySeconds, 12);
        Assert.True(estimator.LastUsedMeasured);
        Assert.Equal(10.0, estimator.LastGameReportedRttSeconds, 12);
        Assert.Equal(2.0, estimator.LastMeasuredRttSeconds, 12);
    }

    [Fact]
    public void GameRttWinsWhenItIsLowerThanTheMeasuredRtt()
    {
        var measured = MeasuredWith(4.0);
        var estimator = new LatencyEstimator(() => 2.0, () => 0.0, () => false, measured);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(1.0, oneWaySeconds, 12);
        Assert.False(estimator.LastUsedMeasured);
        Assert.Equal(4.0, estimator.LastMeasuredRttSeconds, 12);
    }

    [Fact]
    public void InitialRttCapAppliesUntilAMeasuredSampleExists()
    {
        var noSamples = new MeasuredRttEstimator();
        var estimator = new LatencyEstimator(
            () => 10.0, () => 0.0, () => false, noSamples, () => 1.0);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(0.5, oneWaySeconds, 12); // min(10, 1) / 2
        Assert.False(estimator.LastUsedMeasured);
    }

    [Fact]
    public void MeasuredSampleDisablesTheInitialRttCap()
    {
        var measured = MeasuredWith(2.0);
        var estimator = new LatencyEstimator(
            () => 10.0, () => 0.0, () => false, measured, () => 1.0);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(1.0, oneWaySeconds, 12); // min(10, 2) / 2; cap is ignored
    }

    [Fact]
    public void MeasuredSampleIsUsedWhenTheGameReportsNoRttYet()
    {
        var measured = MeasuredWith(1.6);
        var estimator = new LatencyEstimator(() => 0.0, () => 0.0, () => false, measured);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(0.8, oneWaySeconds, 12);
        Assert.True(estimator.LastUsedMeasured);
    }

    [Fact]
    public void NonFiniteInitialCapMeansNoCap()
    {
        var estimator = new LatencyEstimator(
            () => 10.0, () => 0.0, () => false, null, () => double.NaN);

        Assert.True(estimator.TryGetOneWayLatency(out var oneWaySeconds));
        Assert.Equal(5.0, oneWaySeconds, 12);
    }

    [Fact]
    public void TimeoutRttUsesTheLargerOfGameAndMeasured()
    {
        var measured = MeasuredWith(0.6);
        var estimator = new LatencyEstimator(() => 0.26, () => 0.0, () => false, measured);

        Assert.True(estimator.TryGetTimeoutRoundTrip(out var rttSeconds));
        Assert.Equal(0.6, rttSeconds, 12);
    }

    [Fact]
    public void TimeoutRttFallsBackToMeasuredWhenTheGameHasNoReading()
    {
        var measured = MeasuredWith(0.6);
        var estimator = new LatencyEstimator(() => 0.0, () => 0.0, () => false, measured);

        Assert.True(estimator.TryGetTimeoutRoundTrip(out var rttSeconds));
        Assert.Equal(0.6, rttSeconds, 12);
    }

    [Fact]
    public void TimeoutRttKeepsAGameSpikeEvenWhenTheMeasuredMedianIsLower()
    {
        // A 4 s game reading is a spike, but the fallback must not fire before the late
        // arrival; the 3 s configured ceiling still bounds it in PredictionMath.StuckTimeout.
        var measured = MeasuredWith(0.26);
        var estimator = new LatencyEstimator(() => 4.0, () => 0.0, () => false, measured);

        Assert.True(estimator.TryGetTimeoutRoundTrip(out var rttSeconds));
        Assert.Equal(4.0, rttSeconds, 12);
    }

    [Fact]
    public void TimeoutRttIsTheGameValueWithoutSamples()
    {
        var estimator = new LatencyEstimator(() => 0.5, () => 0.0, () => false);

        Assert.True(estimator.TryGetTimeoutRoundTrip(out var rttSeconds));
        Assert.Equal(0.5, rttSeconds, 12);
    }

    [Fact]
    public void HostHasNoTimeoutRtt()
    {
        var estimator = Create(0.5, isHost: true);

        Assert.False(estimator.TryGetTimeoutRoundTrip(out var rttSeconds));
        Assert.Equal(0.0, rttSeconds, 12);
    }

    [Fact]
    public void MissingRttHasNoTimeoutEstimate()
    {
        var estimator = new LatencyEstimator(() => 0.0, () => 0.0, () => false);

        Assert.False(estimator.TryGetTimeoutRoundTrip(out var rttSeconds));
        Assert.Equal(0.0, rttSeconds, 12);
    }
}
