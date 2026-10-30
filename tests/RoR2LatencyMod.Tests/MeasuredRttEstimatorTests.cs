using Xunit;

namespace RoR2LatencyMod.Tests;

public class MeasuredRttEstimatorTests
{
    [Fact]
    public void ArrivalConsumesOldestPendingEnter()
    {
        var estimator = new MeasuredRttEstimator();
        estimator.ObserveEnter(10.0);
        estimator.ObserveEnter(11.0);

        Assert.True(estimator.TryConsumeArrival(12.5, out var first));
        Assert.Equal(2.5, first, 12); // 12.5 - 10.0
        Assert.True(estimator.TryConsumeArrival(12.8, out var second));
        Assert.Equal(1.8, second, 12); // 12.8 - 11.0

        Assert.True(estimator.TryGetMedian(out var median));
        Assert.Equal(2.15, median, 12); // (1.8 + 2.5) / 2
    }

    [Theory]
    [InlineData(0.04)] // below MinSampleSeconds
    [InlineData(8.01)] // above MaxSampleSeconds
    public void SampleOutsideBoundsIsRejected(double sampleSeconds)
    {
        var estimator = new MeasuredRttEstimator();
        estimator.ObserveEnter(10.0);

        Assert.False(estimator.TryConsumeArrival(10.0 + sampleSeconds, out _));
        Assert.Equal(0, estimator.SampleCount);
        Assert.Equal(0, estimator.PendingCount);
    }

    [Fact]
    public void StalePendingIsDroppedInsteadOfProducingALateSample()
    {
        var estimator = new MeasuredRttEstimator();
        estimator.ObserveEnter(0.0);

        Assert.False(estimator.TryConsumeArrival(9.0, out _));
        Assert.Equal(0, estimator.SampleCount);
        Assert.Equal(0, estimator.PendingCount);
    }

    [Fact]
    public void MedianUsesOnlyTheNewestSamplesInTheWindow()
    {
        var estimator = new MeasuredRttEstimator();
        double now = 0.0;
        foreach (var sample in new[] { 0.20, 0.40, 0.60, 0.80, 1.00, 1.20 })
        {
            estimator.ObserveEnter(now);
            Assert.True(estimator.TryConsumeArrival(now + sample, out var got));
            Assert.Equal(sample, got, 12);
            now += 10.0;
        }

        // Last five samples: 0.40, 0.60, 0.80, 1.00, 1.20 -> median 0.80.
        Assert.True(estimator.TryGetMedian(out var median));
        Assert.Equal(0.80, median, 12);
        Assert.Equal(MeasuredRttEstimator.SampleWindow, estimator.SampleCount);
    }

    [Fact]
    public void EvenSampleCountMedianAveragesTheTwoMiddleValues()
    {
        var estimator = new MeasuredRttEstimator();
        double now = 0.0;
        foreach (var sample in new[] { 0.40, 0.80, 1.00, 1.20 })
        {
            estimator.ObserveEnter(now);
            Assert.True(estimator.TryConsumeArrival(now + sample, out _));
            now += 10.0;
        }

        Assert.True(estimator.TryGetMedian(out var median));
        Assert.Equal(0.90, median, 12); // (0.80 + 1.00) / 2
    }

    [Fact]
    public void MedianIgnoresOneLargeWithinBoundsOutlier()
    {
        var estimator = new MeasuredRttEstimator();
        foreach (var sample in new[] { 0.50, 0.60, 5.00 })
        {
            estimator.ObserveEnter(0.0);
            Assert.True(estimator.TryConsumeArrival(sample, out _));
        }

        Assert.True(estimator.TryGetMedian(out var median));
        Assert.Equal(0.60, median, 12); // sorted 0.50, 0.60, 5.00
    }

    [Fact]
    public void PendingQueueIsBounded()
    {
        var estimator = new MeasuredRttEstimator();
        for (var i = 0; i < MeasuredRttEstimator.MaxPending + 3; i++)
        {
            estimator.ObserveEnter(i * 0.1);
        }

        Assert.Equal(MeasuredRttEstimator.MaxPending, estimator.PendingCount);
    }

    [Fact]
    public void ResetClearsPendingAndSamples()
    {
        var estimator = new MeasuredRttEstimator();
        estimator.ObserveEnter(0.0);
        estimator.ObserveEnter(1.0);
        Assert.True(estimator.TryConsumeArrival(2.0, out _));

        estimator.Reset();

        Assert.Equal(0, estimator.PendingCount);
        Assert.Equal(0, estimator.SampleCount);
        Assert.False(estimator.TryGetMedian(out _));
    }

    [Fact]
    public void NonFiniteTimesAreIgnored()
    {
        var estimator = new MeasuredRttEstimator();
        estimator.ObserveEnter(double.NaN);
        estimator.ObserveEnter(double.PositiveInfinity);
        Assert.Equal(0, estimator.PendingCount);

        estimator.ObserveEnter(1.0);
        Assert.False(estimator.TryConsumeArrival(double.NaN, out _));
        Assert.Equal(1, estimator.PendingCount);
    }
}
