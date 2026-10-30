using System.Collections.Generic;
using Xunit;

namespace RoR2LatencyMod.Tests;

public class DiagLogTests
{
    [Fact]
    public void InfoAndWarningAreForwardedUnthrottled()
    {
        var infos = new List<string>();
        var warnings = new List<string>();
        var log = new DiagLog(infos.Add, warnings.Add, () => 0.0);

        log.Info("one");
        log.Info("two");
        log.Warning("three");

        Assert.Equal(new[] { "one", "two" }, infos);
        Assert.Equal(new[] { "three" }, warnings);
    }

    [Fact]
    public void SameKeyIsThrottledForOneSecond()
    {
        var infos = new List<string>();
        double now = 0.0;
        var log = new DiagLog(infos.Add, _ => { }, () => now);

        log.Throttled("rtt", "a");
        now = 0.999;
        log.Throttled("rtt", "b");
        now = 1.0;
        log.Throttled("rtt", "c");

        Assert.Equal(new[] { "a", "c" }, infos);
    }

    [Fact]
    public void DifferentKeysThrottleIndependently()
    {
        var infos = new List<string>();
        double now = 0.0;
        var log = new DiagLog(infos.Add, _ => { }, () => now);

        log.Throttled("u", "u1");
        log.Throttled("lead", "l1");
        now = 0.5;
        log.Throttled("u", "u2");
        log.Throttled("lead", "l2");

        Assert.Equal(new[] { "u1", "l1" }, infos);
    }
}
