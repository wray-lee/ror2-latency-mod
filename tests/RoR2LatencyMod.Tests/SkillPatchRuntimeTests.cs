using System;
using System.Collections.Generic;
using Xunit;

namespace RoR2LatencyMod.Tests;

public class SkillPatchRuntimeTests
{
    private sealed class FakePatch : SkillPatch
    {
        public FakePatch(Func<bool> isEnabledByConfig)
            : base("Fake", PatchSide.Client, isEnabledByConfig)
        {
        }

        protected override void Install()
        {
        }
    }

    [Fact]
    public void ShouldRunReadsSwitchLiveAndRequiresRemoteClient()
    {
        var enabled = true;
        var patch = new FakePatch(() => enabled);

        Assert.False(patch.ShouldRun());

        patch.TryInstall(_ => { });
        Assert.True(patch.ShouldRun());

        enabled = false;
        Assert.False(patch.ShouldRun());
        enabled = true;

        try
        {
            SkillPatch.IsHostDetector = () => true;
            Assert.False(patch.ShouldRun());
        }
        finally
        {
            SkillPatch.IsHostDetector = () => false;
        }

        Assert.True(patch.ShouldRun());
    }

    private sealed class LocalUiPatch : SkillPatch
    {
        public LocalUiPatch(Func<bool> isEnabledByConfig, PatchSide side)
            : base("Local UI", side, isEnabledByConfig)
        {
        }

        protected override bool RequiresRemoteClient => false;

        protected override void Install()
        {
        }
    }

    [Fact]
    public void LocalUiPatchRunsOnHostButNetworkPatchDoesNot()
    {
        var local = new LocalUiPatch(() => true, PatchSide.Client);
        var network = new FakePatch(() => true);
        local.TryInstall(_ => { });
        network.TryInstall(_ => { });

        try
        {
            SkillPatch.IsHostDetector = () => true;
            Assert.True(local.ShouldRun());
            Assert.False(network.ShouldRun());
        }
        finally
        {
            SkillPatch.IsHostDetector = () => false;
        }
    }

    [Fact]
    public void RuntimeGateMatrixCoversInstallSwitchTripAndHost()
    {
        // LoaderFlyTimeoutPatch.RuntimeActive is exactly instance != null && ShouldRun(); this
        // locks the state matrix the crosshair depends on (no Unity object needed here).
        var enabled = true;
        var patch = new FakePatch(() => enabled);

        Assert.False(patch.ShouldRun()); // not installed

        patch.TryInstall(_ => { });
        Assert.True(patch.ShouldRun());  // installed + enabled + not host

        enabled = false;
        Assert.False(patch.ShouldRun()); // live switch off
        enabled = true;

        try
        {
            SkillPatch.IsHostDetector = () => true;
            Assert.False(patch.ShouldRun()); // host: FlyTimeout is inert
        }
        finally
        {
            SkillPatch.IsHostDetector = () => false;
        }

        Assert.True(patch.ShouldRun());
        patch.Guard(() => throw new InvalidOperationException("trip"));
        Assert.False(patch.ShouldRun()); // tripped
    }

    [Fact]
    public void GuardTripsOnExceptionAndShortCircuitsLaterCalls()
    {
        var logs = new List<string>();
        var patch = new FakePatch(() => true);
        patch.TryInstall(logs.Add);

        var calls = 0;
        Assert.True(patch.Guard(() => calls++));
        Assert.False(patch.Guard(() => throw new InvalidOperationException("boom")));

        Assert.True(patch.IsTripped);
        Assert.False(patch.ShouldRun());
        Assert.False(patch.Guard(() => calls++));

        Assert.Equal(1, calls);
        Assert.Contains(logs, line => line.Contains("tripped") && line.Contains("boom"));
    }
}
