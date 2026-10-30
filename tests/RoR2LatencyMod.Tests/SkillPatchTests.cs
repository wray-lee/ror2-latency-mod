using System;
using System.Collections.Generic;
using Xunit;

namespace RoR2LatencyMod.Tests;

public class SkillPatchTests
{
    private sealed class FakePatch : SkillPatch
    {
        private readonly Action onInstall;

        public FakePatch(
            string name,
            PatchSide side,
            Func<bool> isEnabledByConfig,
            Action onInstall = null)
            : base(name, side, isEnabledByConfig)
        {
            this.onInstall = onInstall;
        }

        public int InstallCalls { get; private set; }

        protected override void Install()
        {
            InstallCalls++;
            onInstall?.Invoke();
        }
    }

    [Fact]
    public void PatchExposesNameSideAndStartsInactive()
    {
        var patch = new FakePatch("Loader Hook", PatchSide.Client, () => true);

        Assert.Equal("Loader Hook", patch.Name);
        Assert.Equal(PatchSide.Client, patch.Side);
        Assert.False(patch.IsInstalled);
        Assert.False(patch.IsEnabled);
    }

    [Fact]
    public void InstallerInstallsPatchWhoseSwitchIsOn()
    {
        var patch = new FakePatch("Loader Hook", PatchSide.Client, () => true);
        var installer = new SkillPatchInstaller(_ => { }, () => false, patch);

        installer.InstallAll();

        Assert.True(patch.IsInstalled);
        Assert.True(patch.IsEnabled);
        Assert.Equal(1, patch.InstallCalls);
    }

    [Fact]
    public void InstallerSkipsPatchWhoseSwitchIsOff()
    {
        var logs = new List<string>();
        var patch = new FakePatch("Loader Hook", PatchSide.Client, () => false);
        var installer = new SkillPatchInstaller(logs.Add, () => false, patch);

        installer.InstallAll();

        Assert.False(patch.IsInstalled);
        Assert.False(patch.IsEnabled);
        Assert.Equal(0, patch.InstallCalls);
        Assert.Contains(logs, line => line.Contains("Loader Hook") && line.Contains("disabled"));
    }

    [Fact]
    public void EachPatchHasItsOwnSwitch()
    {
        var on = new FakePatch("On", PatchSide.Client, () => true);
        var off = new FakePatch("Off", PatchSide.Client, () => false);
        var installer = new SkillPatchInstaller(_ => { }, () => false, on, off);

        installer.InstallAll();

        Assert.True(on.IsEnabled);
        Assert.False(off.IsEnabled);
    }

    [Fact]
    public void FailingPatchIsDisabledWithoutAffectingSiblings()
    {
        var logs = new List<string>();
        var broken = new FakePatch(
            "Broken",
            PatchSide.Client,
            () => true,
            () => throw new MissingMethodException("Target method not found"));
        var sibling = new FakePatch("Sibling", PatchSide.Client, () => true);
        var installer = new SkillPatchInstaller(logs.Add, () => false, broken, sibling);

        installer.InstallAll();

        Assert.False(broken.IsInstalled);
        Assert.False(broken.IsEnabled);
        Assert.True(sibling.IsInstalled);
        Assert.True(sibling.IsEnabled);
        Assert.Equal(1, sibling.InstallCalls);
        Assert.Contains(logs, line => line.Contains("Broken") && line.Contains("failed"));
    }

    [Fact]
    public void HostPatchIsNotInstalledWhenNotHost()
    {
        var logs = new List<string>();
        var hostPatch = new FakePatch("Host Only", PatchSide.Host, () => true);
        var clientPatch = new FakePatch("Client Only", PatchSide.Client, () => true);
        var installer = new SkillPatchInstaller(logs.Add, () => false, hostPatch, clientPatch);

        installer.InstallAll();

        Assert.Equal(0, hostPatch.InstallCalls);
        Assert.False(hostPatch.IsInstalled);
        Assert.True(clientPatch.IsInstalled);
        Assert.Contains(logs, line => line.Contains("Host Only") && line.Contains("not the host"));
    }

    [Fact]
    public void HostPatchIsInstalledWhenHost()
    {
        var hostPatch = new FakePatch("Host Only", PatchSide.Host, () => true);
        var installer = new SkillPatchInstaller(_ => { }, () => true, hostPatch);

        installer.InstallAll();

        Assert.True(hostPatch.IsInstalled);
        Assert.Equal(1, hostPatch.InstallCalls);
    }
}
