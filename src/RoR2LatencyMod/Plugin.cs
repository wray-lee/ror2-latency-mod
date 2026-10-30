using System;
using System.Linq;
using BepInEx;
using R2API.Utils;
using RoR2;
using UnityEngine;

namespace RoR2LatencyMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.bepis.r2api")]
[BepInDependency("com.rune580.riskofoptions", BepInDependency.DependencyFlags.SoftDependency)]
[NetworkCompatibility(CompatibilityLevel.NoNeedForSync)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.wray.ror2latency";
    public const string PluginName = "RoR2 Latency Mod";
    public const string PluginVersion = "0.1.0";

    /// <summary>Shared one-way latency estimate; patches read U from here.</summary>
    public static LatencyEstimator Latency { get; private set; }

    private void Awake()
    {
        Logger.LogInfo($"{PluginName} {PluginVersion} ({PluginGuid}) loaded.");
        NetworkListAssertion.Run(Logger);

        SkillPatch.IsHostDetector = GameLatency.IsNetworkServerActive;
        PatchConfig.Bind(Config);
        Latency = GameLatency.Create();
        // Enforcer only: local enter->arrival samples plus a capped initial game RTT.
        // Loader keeps the game-reported estimator it was tuned against.
        var enforcerLatency = GameLatency.CreateEnforcer(
            new MeasuredRttEstimator(),
            () => 2.0 * PatchConfig.InitialLatencyCapSeconds.Value);
        var diag = new DiagLog(Logger.LogInfo, Logger.LogWarning, () => Time.time);

        var patches = new SkillPatch[]
        {
            new LoaderHookPredictionPatch(
                PatchConfig.Toggle(PatchConfig.LoaderHookPrediction),
                diag,
                Latency,
                PatchConfig.Read(PatchConfig.FeedbackFallbackMaxSeconds)),
            new LoaderFlyTimeoutPatch(
                PatchConfig.Toggle(PatchConfig.LoaderFlyTimeout),
                PatchConfig.Read(PatchConfig.LoaderRulerEquivalent),
                PatchConfig.Read(PatchConfig.FlyTimeoutMargin),
                PatchConfig.Read(PatchConfig.FlyTimeoutExtraMaxSeconds),
                PatchConfig.Read(PatchConfig.LoaderHookFallbackMaxSeconds),
                PatchConfig.Read(PatchConfig.LoaderReturnSpeedMultiplier),
                diag, Latency),
            new LoaderCrosshairPatch(
                PatchConfig.Toggle(PatchConfig.LoaderCrosshair), diag),
            new LoaderAimLeadPatch(
                PatchConfig.Toggle(PatchConfig.LoaderAimLead),
                PatchConfig.Read(PatchConfig.AimLeadStrength),
                PatchConfig.Read(PatchConfig.AimLeadMaxSeconds),
                diag, Latency),
            new EnforcerFeedbackPatch(
                PatchConfig.Toggle(PatchConfig.EnforcerFeedback),
                PatchConfig.Read(PatchConfig.FeedbackFallbackTimeout),
                PatchConfig.Read(PatchConfig.FeedbackFallbackMaxSeconds),
                diag, enforcerLatency),
            new EnforcerStartLeadPatch(
                PatchConfig.Toggle(PatchConfig.EnforcerStartLead),
                PatchConfig.Read(PatchConfig.StartLeadCapSeconds),
                PatchConfig.Read(PatchConfig.StartLeadMaxMeters),
                diag, enforcerLatency),
            new EnforcerDashLengthPatch(
                PatchConfig.Read(PatchConfig.EnforcerDashLengthMultiplier), diag),
            new EnforcerExitSmoothingPatch(
                PatchConfig.Toggle(PatchConfig.EnforcerExitSmoothing), PatchConfig.Read(PatchConfig.ExitSmoothSeconds), diag, enforcerLatency),
            new EnforcerGhostDashPatch(
                PatchConfig.Toggle(PatchConfig.EnforcerGhostDash),
                PatchConfig.Read(PatchConfig.FeedbackFallbackTimeout),
                PatchConfig.Read(PatchConfig.FeedbackFallbackMaxSeconds),
                diag, enforcerLatency),
            new EnforcerLocalEndPatch(
                PatchConfig.Toggle(PatchConfig.EnforcerLocalEnd), diag, enforcerLatency),
        };

        new SkillPatchInstaller(Logger.LogInfo, GameLatency.IsNetworkServerActive, patches).InstallAll();
        // BepInEx loads plugins before Language.Init: register RoO after the game language is
        // known so its names/descriptions can use the real language fallback.
        if (RoR2Application.loadFinished)
        {
            RoOIntegration.TryRegister(Logger, Config);
        }
        else
        {
            RoR2Application.onLoad += () => RoOIntegration.TryRegister(Logger, Config);
        }
        LoaderHudModule.Initialize(Logger);

        Logger.LogInfo("[RoR2LatencyMod] patches: " + string.Join(", ",
            patches.Select(patch => $"{patch.Name}={(patch.IsTripped ? "tripped" : patch.InstallStatus)}")));
    }
}
