using System;
using Xunit;

namespace RoR2LatencyMod.Tests;

public class LoaderHudSpeedPunchTests
{
    // ---- 速度文字：字号映射 + 阻尼 ----

    [Theory]
    [InlineData(0.0, 20.0, 32.0, 1.0)]
    [InlineData(50.0, 20.0, 32.0, 1.3)]   // raised cosine at 0.5 -> 26/20
    [InlineData(100.0, 20.0, 32.0, 1.6)]
    [InlineData(1000.0, 20.0, 32.0, 1.6)] // hard cap
    [InlineData(double.NaN, 20.0, 32.0, 1.0)]
    [InlineData(50.0, 0.0, 32.0, 1.0)]
    [InlineData(100.0, 20.0, 10.0, 1.0)]  // max below base collapses to no growth
    public void SpeedFontScaleGrowsWithSpeedAndCaps(
        double speed, double baseSize, double maxSize, double expected)
    {
        Assert.Equal(expected, LoaderHudSpeedTextMath.FontScale(speed, baseSize, maxSize), 12);
    }

    [Fact]
    public void SpeedFontScaleIsDampedTowardTarget()
    {
        double target = LoaderHudSpeedTextMath.FontScale(75.0, 20.0, 32.0);
        Assert.InRange(target, 1.0, 1.6);

        double oneFrame = LoaderHudMath.DampToward(1.0, target, 12.0, 1.0 / 60.0);
        Assert.True(oneFrame > 1.0);
        Assert.True(oneFrame < target);

        double current = 1.0;
        for (int i = 0; i < 120; i++)
        {
            current = LoaderHudMath.DampToward(current, target, 12.0, 1.0 / 60.0);
        }

        Assert.True(current > target - 1e-6);
        Assert.True(current <= target + 1e-9);
    }

    // ---- 速度文字：观战位置差 ----

    [Theory]
    [InlineData(2.0, 0.02, 100.0)]
    [InlineData(2.0, 1.0 / 60.0, 120.0)]
    [InlineData(0.0, 0.02, 0.0)]
    [InlineData(-2.0, 0.02, 0.0)]
    [InlineData(2.0, 0.0, 0.0)]
    [InlineData(2.0, -0.02, 0.0)]
    [InlineData(double.NaN, 0.02, 0.0)]
    public void SpectatorSpeedUsesPositionDeltaOverActualFrameDelta(
        double distance, double frameDelta, double expected)
    {
        Assert.Equal(expected, LoaderHudSpeedTextMath.PositionDeltaSpeed(distance, frameDelta), 9);
    }

    [Fact]
    public void SpeedAndAimTextFormattingIsInvariant()
    {
        Assert.Equal("12.3 m/s", LoaderHudSpeedTextMath.FormatSpeed(12.34));
        Assert.Equal("0.0 m/s", LoaderHudSpeedTextMath.FormatSpeed(-3.0));
        Assert.Equal("0.0 m/s", LoaderHudSpeedTextMath.FormatSpeed(double.NaN));
        Assert.Equal("0.0 m/s", LoaderHudSpeedTextMath.FormatSpeed(double.PositiveInfinity));

        Assert.Equal("0.87", LoaderHudSpeedTextMath.FormatAimDot(0.87));
        Assert.Equal("-0.50", LoaderHudSpeedTextMath.FormatAimDot(-0.5));
        Assert.Equal("1.00", LoaderHudSpeedTextMath.FormatAimDot(2.0));
        Assert.Equal("-1.00", LoaderHudSpeedTextMath.FormatAimDot(-2.0));
        Assert.Equal("0.00", LoaderHudSpeedTextMath.FormatAimDot(double.NaN));
    }

    // ---- 拳速记录：字号与行格式 ----

    [Theory]
    [InlineData(0.0, false, 1.0)]
    [InlineData(50.0, false, 1.3)]
    [InlineData(100.0, false, 1.6)]
    [InlineData(1000.0, false, 1.6)]
    [InlineData(100.0, true, 0.7)] // Thunderslam first: always small
    public void PunchRecordFontScaleGrowsWithSpeedAndSlamIsSmall(
        double speed, bool slammedFirst, double expected)
    {
        Assert.Equal(expected, LoaderHudPunchRecordMath.RecordFontScale(speed, slammedFirst), 12);
    }

    [Fact]
    public void PunchRecordLineShowsAngleColonSpeed()
    {
        Assert.Equal(
            "<size=160%>87 : 120</size>",
            LoaderHudPunchRecordMath.FormatRecordLine(new PunchRecord(120.4, 0.87, false)));
        Assert.Equal(
            "<size=130%>50 : 50</size>",
            LoaderHudPunchRecordMath.FormatRecordLine(new PunchRecord(50.0, 0.5, false)));
        Assert.Equal(
            "<size=160%>0 : 120</size>", // negative dot clamps to 0 for display
            LoaderHudPunchRecordMath.FormatRecordLine(new PunchRecord(120.4, -0.6, false)));
    }

    [Fact]
    public void SlammedFirstRecordIsGrayAndSmall()
    {
        Assert.Equal(
            "<color=#808080><size=70%>87 : 120</size></color>",
            LoaderHudPunchRecordMath.FormatRecordLine(new PunchRecord(120.4, 0.87, true)));
    }

    // ---- 拳速记录：一次拳击一条（多目标去重） ----

    [Fact]
    public void TrackerRecordsOnlyFirstHitOfOnePunch()
    {
        var tracker = new LoaderHudPunchTracker();
        tracker.BeginPunch(7, 120.0, 0.9);

        Assert.True(tracker.TryRecordHit(7, 10.0));  // first target
        Assert.False(tracker.TryRecordHit(7, 10.0)); // same punch, second target
        Assert.False(tracker.TryRecordHit(7, 10.1)); // same punch, later overlap tick

        PunchRecord[] records = tracker.Snapshot(7, 5);
        Assert.Single(records);
        Assert.Equal(120.0, records[0].Speed, 12);
    }

    [Fact]
    public void TrackerKeepsNewestNAndSortsNewestFirst()
    {
        var tracker = new LoaderHudPunchTracker();
        for (int i = 1; i <= 4; i++)
        {
            tracker.BeginPunch(7, i * 10.0, 0.5);
            Assert.True(tracker.TryRecordHit(7, i));
        }

        PunchRecord[] records = tracker.Snapshot(7, 2);
        Assert.Equal(2, records.Length);
        Assert.Equal(40.0, records[0].Speed, 12);
        Assert.Equal(30.0, records[1].Speed, 12);
    }

    // ---- 拳速记录：灰化窗口（Thunderslam 先于首次拳头命中） ----

    [Fact]
    public void SlamBetweenPunchStartAndFirstHitMarksRecordGray()
    {
        var tracker = new LoaderHudPunchTracker();
        tracker.BeginPunch(7, 120.0, 0.9);
        tracker.ObserveSlam(7, 9.0, true);
        Assert.True(tracker.TryRecordHit(7, 10.0));

        Assert.True(tracker.Snapshot(7, 1)[0].SlammedFirst);
    }

    [Theory]
    [InlineData(false)] // slam hit nothing
    [InlineData(true)]  // slam after the punch hit
    public void SlamOutsideWindowDoesNotMarkRecordGray(bool slamAfterHit)
    {
        var tracker = new LoaderHudPunchTracker();
        tracker.BeginPunch(7, 120.0, 0.9);
        if (slamAfterHit)
        {
            Assert.True(tracker.TryRecordHit(7, 10.0));
            tracker.ObserveSlam(7, 11.0, true);
        }
        else
        {
            tracker.ObserveSlam(7, 9.0, false);
            Assert.True(tracker.TryRecordHit(7, 10.0));
        }

        Assert.False(tracker.Snapshot(7, 1)[0].SlammedFirst);
    }

    [Fact]
    public void SlamBeforePunchStartDoesNotMarkRecordGray()
    {
        var tracker = new LoaderHudPunchTracker();
        tracker.ObserveSlam(7, 1.0, true); // no pending punch yet
        tracker.BeginPunch(7, 120.0, 0.9);
        Assert.True(tracker.TryRecordHit(7, 2.0));

        Assert.False(tracker.Snapshot(7, 1)[0].SlammedFirst);
    }

    [Fact]
    public void TrackerSeparatesBodiesAndClearResets()
    {
        var tracker = new LoaderHudPunchTracker();
        tracker.BeginPunch(1, 10.0, 0.5);
        tracker.BeginPunch(2, 20.0, 0.5);
        Assert.True(tracker.TryRecordHit(1, 1.0));
        Assert.True(tracker.TryRecordHit(2, 1.0));

        PunchRecord[] first = tracker.Snapshot(1, 5);
        PunchRecord[] second = tracker.Snapshot(2, 5);
        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(10.0, first[0].Speed, 12);
        Assert.Equal(20.0, second[0].Speed, 12);

        tracker.Clear();
        Assert.Empty(tracker.Snapshot(1, 5));
        Assert.Empty(tracker.Snapshot(2, 5));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    [InlineData(99, 10)]
    public void RecordCountIsClamped(int requested, int expected)
    {
        Assert.Equal(expected, LoaderHudPunchRecordMath.ClampRecordCount(requested));
        Assert.Equal(3, LoaderHudPunchRecordMath.DefaultRecordCount);
    }
}
