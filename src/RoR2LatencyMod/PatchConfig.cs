using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace RoR2LatencyMod;

/// <summary>
/// Per-HUD settings. Every HUD owns one of these; all values are read live by the
/// HUD element. The only exception is startup: with every HUD off the module installs
/// no HUD hook, so the first enable (and re-enabling after the last HUD was turned off)
/// needs a restart.
/// </summary>
public sealed class LoaderHudConfig
{
    public const float AnchorMin = 0f;
    public const float AnchorMax = 1f;
    public const float OffsetMin = -0.5f;
    public const float OffsetMax = 0.5f;
    public const float ScaleMin = 0.5f;
    public const float ScaleMax = 3f;
    public const float FontSizeMin = 8f;
    public const float FontSizeMax = 48f;

    internal LoaderHudConfig(string key, string displayNameKey)
    {
        Key = key;
        DisplayNameKey = displayNameKey;
    }

    public string Key { get; }
    public string DisplayNameKey { get; }
    public string DisplayName => LabelTable.Get(DisplayNameKey, LabelLanguageProbe.Startup);

    public ConfigEntry<bool> Enabled { get; internal set; }
    public ConfigEntry<float> AnchorX { get; internal set; }
    public ConfigEntry<float> AnchorY { get; internal set; }
    public ConfigEntry<float> OffsetX { get; internal set; }
    public ConfigEntry<float> OffsetY { get; internal set; }
    public ConfigEntry<float> Scale { get; internal set; }
    public ConfigEntry<Color> TextColor { get; internal set; }
    public ConfigEntry<float> Opacity { get; internal set; }
    public ConfigEntry<float> FontSize { get; internal set; }
    public ConfigEntry<float> BackgroundOpacity { get; internal set; }
}

/// <summary>
/// Binds each Skill Patch to its own BepInEx config switch. Patches stay
/// BepInEx-free so the framework and its tests never load the game assemblies.
/// Values are exposed as Funcs so edits apply on the next hook call.
/// </summary>
public static class PatchConfig
{
    public static ConfigEntry<bool> LoaderHookPrediction { get; private set; }
    public static ConfigEntry<bool> LoaderFlyTimeout { get; private set; }
    public static ConfigEntry<bool> LoaderRulerEquivalent { get; private set; }
    public static ConfigEntry<bool> LoaderCrosshair { get; private set; }
    public static ConfigEntry<bool> LoaderAimLead { get; private set; }
    public static ConfigEntry<bool> EnforcerFeedback { get; private set; }
    public static ConfigEntry<bool> EnforcerStartLead { get; private set; }
    public static ConfigEntry<bool> EnforcerExitSmoothing { get; private set; }
    public static ConfigEntry<bool> EnforcerGhostDash { get; private set; }
    public static ConfigEntry<bool> EnforcerLocalEnd { get; private set; }

    // Loader HUD: one independent group per display element, all off by default.
    public static LoaderHudConfig HudGrappleDuration { get; private set; }
    public static LoaderHudConfig HudGrappleLength { get; private set; }
    public static LoaderHudConfig HudSpeedText { get; private set; }
    public static LoaderHudConfig HudPunchRecord { get; private set; }
    public static LoaderHudConfig HudVelocityIcon { get; private set; }
    public static LoaderHudConfig HudGrappleEnergy { get; private set; }

    public static IReadOnlyList<LoaderHudConfig> LoaderHuds { get; private set; } =
        Array.Empty<LoaderHudConfig>();

    public static ConfigEntry<float> AimLeadStrength { get; private set; }
    public static ConfigEntry<float> AimLeadMaxSeconds { get; private set; }
    public static ConfigEntry<float> FlyTimeoutMargin { get; private set; }
    public static ConfigEntry<float> FlyTimeoutExtraMaxSeconds { get; private set; }
    public static ConfigEntry<float> LoaderHookFallbackMaxSeconds { get; private set; }
    public static ConfigEntry<float> LoaderReturnSpeedMultiplier { get; private set; }
    public static ConfigEntry<float> FeedbackFallbackTimeout { get; private set; }
    public static ConfigEntry<float> FeedbackFallbackMaxSeconds { get; private set; }
    public static ConfigEntry<float> InitialLatencyCapSeconds { get; private set; }
    public static ConfigEntry<float> StartLeadCapSeconds { get; private set; }
    public static ConfigEntry<float> StartLeadMaxMeters { get; private set; }
    public static ConfigEntry<float> EnforcerDashLengthMultiplier { get; private set; }
    public static ConfigEntry<float> ExitSmoothSeconds { get; private set; }

    /// <summary>Registers every entry once, from <c>Plugin.Awake</c>.</summary>
    public static void Bind(ConfigFile config)
    {
        LabelLanguage language = LabelLanguageProbe.Startup;

        LoaderHookPrediction = config.Bind(
            "Loader", "LoaderHookPrediction", true,
            LabelTable.Get("patch.LoaderHookPrediction.desc", language));

        LoaderFlyTimeout = config.Bind(
            "Loader", "LoaderFlyTimeout", true,
            LabelTable.Get("patch.LoaderFlyTimeout.desc", language));

        LoaderRulerEquivalent = config.Bind(
            "Loader", "LoaderRulerEquivalent", true,
            LabelTable.Get("patch.LoaderRulerEquivalent.desc", language));

        LoaderCrosshair = config.Bind(
            "Loader", "LoaderCrosshair", true,
            LabelTable.Get("patch.LoaderCrosshair.desc", language));

        LoaderAimLead = config.Bind(
            "Loader", "LoaderAimLead", true,
            LabelTable.Get("patch.LoaderAimLead.desc", language));

        AimLeadStrength = config.Bind(
            "Loader", "AimLeadStrength", 1f,
            new ConfigDescription(
                LabelTable.Get("patch.AimLeadStrength.desc", language),
                new AcceptableValueRange<float>(0f, 3f)));

        AimLeadMaxSeconds = config.Bind(
            "Loader", "AimLeadMaxSeconds", 0.15f,
            new ConfigDescription(
                LabelTable.Get("patch.AimLeadMaxSeconds.desc", language),
                new AcceptableValueRange<float>(0f, 0.5f)));

        FlyTimeoutMargin = config.Bind(
            "Loader", "FlyTimeoutMargin", 0.1f,
            new ConfigDescription(
                LabelTable.Get("patch.FlyTimeoutMargin.desc", language),
                new AcceptableValueRange<float>(0f, 4f)));

        FlyTimeoutExtraMaxSeconds = config.Bind(
            "Loader", "FlyTimeoutExtraMaxSeconds", 0.15f,
            new ConfigDescription(
                LabelTable.Get("patch.FlyTimeoutExtraMaxSeconds.desc", language),
                new AcceptableValueRange<float>(0f, 4f)));

        LoaderHookFallbackMaxSeconds = config.Bind(
            "Loader", "LoaderHookFallbackMaxSeconds", 3f,
            new ConfigDescription(
                LabelTable.Get("patch.LoaderHookFallbackMaxSeconds.desc", language),
                new AcceptableValueRange<float>(0.5f, 10f)));

        LoaderReturnSpeedMultiplier = config.Bind(
            "Loader", "LoaderReturnSpeedMultiplier", 1f,
            new ConfigDescription(
                LabelTable.Get("patch.LoaderReturnSpeedMultiplier.desc", language),
                new AcceptableValueRange<float>(1f, 10f)));

        EnforcerFeedback = config.Bind(
            "Enforcer", "EnforcerFeedback", true,
            LabelTable.Get("patch.EnforcerFeedback.desc", language));

        FeedbackFallbackTimeout = config.Bind(
            "Enforcer", "FeedbackFallbackTimeout", 0f,
            new ConfigDescription(
                LabelTable.Get("patch.FeedbackFallbackTimeout.desc", language),
                new AcceptableValueRange<float>(0f, 10f)));

        FeedbackFallbackMaxSeconds = config.Bind(
            "Enforcer", "FeedbackFallbackMaxSeconds", 3f,
            new ConfigDescription(
                LabelTable.Get("patch.FeedbackFallbackMaxSeconds.desc", language),
                new AcceptableValueRange<float>(0.5f, 10f)));

        InitialLatencyCapSeconds = config.Bind(
            "Enforcer", "InitialLatencyCapSeconds", 0.5f,
            new ConfigDescription(
                LabelTable.Get("patch.InitialLatencyCapSeconds.desc", language),
                new AcceptableValueRange<float>(0.05f, 2f)));

        EnforcerStartLead = config.Bind(
            "Enforcer", "EnforcerStartLead", true,
            LabelTable.Get("patch.EnforcerStartLead.desc", language));

        StartLeadCapSeconds = config.Bind(
            "Enforcer", "StartLeadCapSeconds", 0.25f,
            new ConfigDescription(
                LabelTable.Get("patch.StartLeadCapSeconds.desc", language),
                new AcceptableValueRange<float>(0f, 1f)));

        StartLeadMaxMeters = config.Bind(
            "Enforcer", "StartLeadMaxMeters", 0f,
            new ConfigDescription(
                LabelTable.Get("patch.StartLeadMaxMeters.desc", language),
                new AcceptableValueRange<float>(0f, 20f)));

        EnforcerDashLengthMultiplier = config.Bind(
            "Enforcer", "EnforcerDashLengthMultiplier", 1f,
            new ConfigDescription(
                LabelTable.Get("patch.EnforcerDashLengthMultiplier.desc", language),
                new AcceptableValueRange<float>(1f, 10f)));

        EnforcerExitSmoothing = config.Bind(
            "Enforcer", "EnforcerExitSmoothing", true,
            LabelTable.Get("patch.EnforcerExitSmoothing.desc", language));

        EnforcerGhostDash = config.Bind(
            "Enforcer", "EnforcerGhostDash", true,
            LabelTable.Get("patch.EnforcerGhostDash.desc", language));

        EnforcerLocalEnd = config.Bind(
            "Enforcer", "EnforcerLocalEnd", true,
            LabelTable.Get("patch.EnforcerLocalEnd.desc", language));

        ExitSmoothSeconds = config.Bind(
            "Enforcer", "ExitSmoothSeconds", 0.15f,
            new ConfigDescription(
                LabelTable.Get("patch.ExitSmoothSeconds.desc", language),
                new AcceptableValueRange<float>(0.1f, 0.2f)));

        // HUD 默认全部关闭。锚点 (0.5, 0.5) = HUD 安全区中心（准星附近）；
        // 偏移是父容器宽/高的比例，因此换分辨率或 UI 缩放不会漂到屏幕外。
        HudGrappleDuration = BindHud(config, "GrappleDuration", "hud.name.grappleDuration",
            0.5f, 0.5f, 0.055f, 0.100f, 22f);
        HudGrappleLength = BindHud(config, "GrappleLength", "hud.name.grappleLength",
            0.5f, 0.5f, 0.055f, 0.065f, 18f);
        HudSpeedText = BindHud(config, "SpeedText", "hud.name.speedText",
            0.5f, 0.5f, 0.110f, -0.060f, 20f);
        HudPunchRecord = BindHud(config, "PunchRecord", "hud.name.punchRecord",
            0.5f, 0.5f, -0.220f, 0.120f, 18f);
        HudVelocityIcon = BindHud(config, "VelocityIcon", "hud.name.velocityIcon",
            0.5f, 0.5f, -0.090f, -0.080f, 24f);
        HudGrappleEnergy = BindHud(config, "GrappleEnergy", "hud.name.grappleEnergy",
            0.5f, 0.5f, 0.120f, 0.020f, 16f);

        LoaderHuds = new[]
        {
            HudGrappleDuration, HudGrappleLength, HudSpeedText,
            HudPunchRecord, HudVelocityIcon, HudGrappleEnergy,
        };
    }

    private static LoaderHudConfig BindHud(
        ConfigFile config, string key, string displayNameKey,
        float anchorX, float anchorY, float offsetX, float offsetY, float fontSize)
    {
        const string section = "Loader HUD";
        string prefix = "HUD_" + key + "_";
        LabelLanguage language = LabelLanguageProbe.Startup;
        string displayName = LabelTable.Get(displayNameKey, language);
        var hud = new LoaderHudConfig(key, displayNameKey);

        hud.Enabled = config.Bind(
            section, prefix + "Enabled", false,
            LabelTable.Format("hud.desc.enabled", language, displayName));

        hud.AnchorX = config.Bind(
            section, prefix + "AnchorX", anchorX,
            new ConfigDescription(
                LabelTable.Format("hud.desc.anchorX", language, displayName, anchorX),
                new AcceptableValueRange<float>(LoaderHudConfig.AnchorMin, LoaderHudConfig.AnchorMax)));
        hud.AnchorY = config.Bind(
            section, prefix + "AnchorY", anchorY,
            new ConfigDescription(
                LabelTable.Format("hud.desc.anchorY", language, displayName, anchorY),
                new AcceptableValueRange<float>(LoaderHudConfig.AnchorMin, LoaderHudConfig.AnchorMax)));
        hud.OffsetX = config.Bind(
            section, prefix + "OffsetX", offsetX,
            new ConfigDescription(
                LabelTable.Format("hud.desc.offsetX", language, displayName, offsetX),
                new AcceptableValueRange<float>(LoaderHudConfig.OffsetMin, LoaderHudConfig.OffsetMax)));
        hud.OffsetY = config.Bind(
            section, prefix + "OffsetY", offsetY,
            new ConfigDescription(
                LabelTable.Format("hud.desc.offsetY", language, displayName, offsetY),
                new AcceptableValueRange<float>(LoaderHudConfig.OffsetMin, LoaderHudConfig.OffsetMax)));
        hud.Scale = config.Bind(
            section, prefix + "Scale", 1f,
            new ConfigDescription(
                LabelTable.Format("hud.desc.scale", language, displayName),
                new AcceptableValueRange<float>(LoaderHudConfig.ScaleMin, LoaderHudConfig.ScaleMax)));
        hud.TextColor = config.Bind(
            section, prefix + "Color", new Color(1f, 0.94f, 0.85f, 1f),
            LabelTable.Format("hud.desc.color", language, displayName));
        hud.Opacity = config.Bind(
            section, prefix + "Opacity", 1f,
            new ConfigDescription(
                LabelTable.Format("hud.desc.opacity", language, displayName),
                new AcceptableValueRange<float>(0f, 1f)));
        hud.FontSize = config.Bind(
            section, prefix + "FontSize", fontSize,
            new ConfigDescription(
                LabelTable.Format("hud.desc.fontSize", language, displayName, fontSize),
                new AcceptableValueRange<float>(LoaderHudConfig.FontSizeMin, LoaderHudConfig.FontSizeMax)));
        hud.BackgroundOpacity = config.Bind(
            section, prefix + "BackgroundOpacity", 0.25f,
            new ConfigDescription(
                LabelTable.Format("hud.desc.backgroundOpacity", language, displayName),
                new AcceptableValueRange<float>(0f, 1f)));

        return hud;
    }

    public static Func<bool> Toggle(ConfigEntry<bool> entry) => () => entry.Value;

    public static Func<T> Read<T>(ConfigEntry<T> entry) => () => entry.Value;
}
