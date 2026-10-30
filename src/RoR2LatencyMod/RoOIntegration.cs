using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using RiskOfOptions;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;

namespace RoR2LatencyMod;

/// <summary>
/// Risk of Options 软依赖（GUID 已核对 RiskOfOptions.dll 的 BepInPlugin 特性：com.rune580.riskofoptions）。
/// 所有 RoO 类型只出现在 Register 里；未装 RoO 时该方法不会被 JIT，插件照常启动、配置仍走配置文件。
/// 名称/描述/说明统一取自 LabelTable。注册推迟到游戏加载完成（语言已初始化），因此按游戏语言回退。
/// </summary>
public static class RoOIntegration
{
    private const string RoOGuid = "com.rune580.riskofoptions";

    public static void TryRegister(ManualLogSource logger, ConfigFile configFile)
    {
        try
        {
            if (!Chainloader.PluginInfos.ContainsKey(RoOGuid))
            {
                logger?.LogInfo($"[RoR2LatencyMod] RoO 不可用，改配置文件：{configFile.ConfigFilePath}");
                return;
            }

            Register();
            logger?.LogInfo(
                "[RoR2LatencyMod] 已注册 Risk of Options 条目（滑条即时生效；" +
                "启动时关闭的补丁开关、以及全关时的 HUD 首次启用，需要重启）。");
        }
        catch (Exception ex)
        {
            // 包括 TypeLoadException/MissingMethodException（RoO 版本不匹配）；只降级，不影响任何补丁。
            logger?.LogWarning(
                $"[RoR2LatencyMod] RoO 不可用，改配置文件：{configFile.ConfigFilePath}（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    // NoInlining：RoO 缺失时 TryRegister 不得因内联而提前解析 RiskOfOptions 类型。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Register()
    {
        LabelLanguage language = LabelLanguageProbe.Current;

        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.LoaderHookPrediction, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.LoaderHookPrediction.name", language),
                description = LabelTable.Get("patch.LoaderHookPrediction.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.LoaderFlyTimeout, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.LoaderFlyTimeout.name", language),
                description = LabelTable.Get("patch.LoaderFlyTimeout.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.LoaderRulerEquivalent, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.LoaderRulerEquivalent.name", language),
                description = LabelTable.Get("patch.LoaderRulerEquivalent.desc", language),
            }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.LoaderCrosshair, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.LoaderCrosshair.name", language),
                description = LabelTable.Get("patch.LoaderCrosshair.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.LoaderAimLead, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.LoaderAimLead.name", language),
                description = LabelTable.Get("patch.LoaderAimLead.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.AimLeadStrength, new SliderConfig
        {
            name = LabelTable.Get("patch.AimLeadStrength.name", language),
            description = LabelTable.Get("patch.AimLeadStrength.desc", language),
            min = 0f,
            max = 3f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.AimLeadMaxSeconds, new SliderConfig
        {
            name = LabelTable.Get("patch.AimLeadMaxSeconds.name", language),
            description = LabelTable.Get("patch.AimLeadMaxSeconds.desc", language),
            min = 0f,
            max = 0.5f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.FlyTimeoutMargin, new SliderConfig
        {
            name = LabelTable.Get("patch.FlyTimeoutMargin.name", language),
            description = LabelTable.Get("patch.FlyTimeoutMargin.desc", language),
            min = 0f,
            max = 4f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.FlyTimeoutExtraMaxSeconds, new SliderConfig
        {
            name = LabelTable.Get("patch.FlyTimeoutExtraMaxSeconds.name", language),
            description = LabelTable.Get("patch.FlyTimeoutExtraMaxSeconds.desc", language),
            min = 0f,
            max = 4f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.LoaderHookFallbackMaxSeconds, new SliderConfig
        {
            name = LabelTable.Get("patch.LoaderHookFallbackMaxSeconds.name", language),
            description = LabelTable.Get("patch.LoaderHookFallbackMaxSeconds.desc", language),
            min = 0.5f,
            max = 10f,
            FormatString = "{0:0.0}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.LoaderReturnSpeedMultiplier, new SliderConfig
        {
            name = LabelTable.Get("patch.LoaderReturnSpeedMultiplier.name", language),
            description = LabelTable.Get("patch.LoaderReturnSpeedMultiplier.desc", language),
            min = 1f,
            max = 10f,
            FormatString = "{0:0.0}",
        }));

        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.EnforcerFeedback, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.EnforcerFeedback.name", language),
                description = LabelTable.Get("patch.EnforcerFeedback.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.FeedbackFallbackTimeout, new SliderConfig
        {
            name = LabelTable.Get("patch.FeedbackFallbackTimeout.name", language),
            description = LabelTable.Get("patch.FeedbackFallbackTimeout.desc", language),
            min = 0f,
            max = 10f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.FeedbackFallbackMaxSeconds, new SliderConfig
        {
            name = LabelTable.Get("patch.FeedbackFallbackMaxSeconds.name", language),
            description = LabelTable.Get("patch.FeedbackFallbackMaxSeconds.desc", language),
            min = 0.5f,
            max = 10f,
            FormatString = "{0:0.0}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.InitialLatencyCapSeconds, new SliderConfig
        {
            name = LabelTable.Get("patch.InitialLatencyCapSeconds.name", language),
            description = LabelTable.Get("patch.InitialLatencyCapSeconds.desc", language),
            min = 0.05f,
            max = 2f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.EnforcerStartLead, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.EnforcerStartLead.name", language),
                description = LabelTable.Get("patch.EnforcerStartLead.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.StartLeadCapSeconds, new SliderConfig
        {
            name = LabelTable.Get("patch.StartLeadCapSeconds.name", language),
            description = LabelTable.Get("patch.StartLeadCapSeconds.desc", language),
            min = 0f,
            max = 1f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.StartLeadMaxMeters, new SliderConfig
        {
            name = LabelTable.Get("patch.StartLeadMaxMeters.name", language),
            description = LabelTable.Get("patch.StartLeadMaxMeters.desc", language),
            min = 0f,
            max = 20f,
            FormatString = "{0:0.0}",
        }));
        ModSettingsManager.AddOption(new SliderOption(PatchConfig.EnforcerDashLengthMultiplier, new SliderConfig
        {
            name = LabelTable.Get("patch.EnforcerDashLengthMultiplier.name", language),
            description = LabelTable.Get("patch.EnforcerDashLengthMultiplier.desc", language),
            min = 1f,
            max = 10f,
            FormatString = "{0:0.0}",
        }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.EnforcerExitSmoothing, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.EnforcerExitSmoothing.name", language),
                description = LabelTable.Get("patch.EnforcerExitSmoothing.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new StepSliderOption(PatchConfig.ExitSmoothSeconds, new StepSliderConfig
        {
            name = LabelTable.Get("patch.ExitSmoothSeconds.name", language),
            description = LabelTable.Get("patch.ExitSmoothSeconds.desc", language),
            min = 0.1f,
            max = 0.2f,
            increment = 0.01f,
            FormatString = "{0:0.00}",
        }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.EnforcerGhostDash, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.EnforcerGhostDash.name", language),
                description = LabelTable.Get("patch.EnforcerGhostDash.desc", language),
                restartRequired = true,
            }));
        ModSettingsManager.AddOption(new CheckBoxOption(
            PatchConfig.EnforcerLocalEnd, new CheckBoxConfig
            {
                name = LabelTable.Get("patch.EnforcerLocalEnd.name", language),
                description = LabelTable.Get("patch.EnforcerLocalEnd.desc", language),
                restartRequired = true,
            }));

        RegisterLoaderHudOptions(language);
    }

    // 每个 HUD：开关 + 颜色（RoO ColorOption 直接吃 ConfigEntry<Color>，无需 RGB 滑条）
    // + 字号/缩放/透明度/底色 + 锚点/偏移。锚点偏移是两个 float（RoO 没有 Vector2 选项）。
    // 开关 restartRequired：全关启动时模块不装任何 HUD 钩子，开启后需重启；
    // 启动时已有任一项开启，则运行期开关即时生效。
    private static void RegisterLoaderHudOptions(LabelLanguage language)
    {
        const string category = "Loader HUD";
        var huds = PatchConfig.LoaderHuds;
        // 启动时已有任一 HUD 开启 → HUD.Awake 钩子已装，之后开关即时生效；全关启动才需重启。
        bool hudRestartRequired = !LoaderHudModule.AnyEnabled();
        for (int i = 0; i < huds.Count; i++)
        {
            LoaderHudConfig hud = huds[i];
            string displayName = LabelTable.Get(hud.DisplayNameKey, language);

            ModSettingsManager.AddOption(new CheckBoxOption(hud.Enabled, new CheckBoxConfig
            {
                name = LabelTable.Format("hud.option.enable", language, displayName),
                description = LabelTable.Format("hud.desc.enabled", language, displayName),
                category = category,
                restartRequired = hudRestartRequired,
            }));
            ModSettingsManager.AddOption(new ColorOption(hud.TextColor, new ColorOptionConfig
            {
                name = LabelTable.Format("hud.option.color", language, displayName),
                description = LabelTable.Format("hud.desc.color", language, displayName),
                category = category,
            }));
            // 速度图标只吃颜色/透明度/缩放/锚点/偏移，字号与底色对它无效，不注册死滑条。
            bool isVelocityIcon = ReferenceEquals(hud, PatchConfig.HudVelocityIcon);
            if (!isVelocityIcon)
            {
                AddHudSlider(hud.FontSize, "hud.option.fontSize", "hud.desc.fontSize", displayName,
                    language, category, LoaderHudConfig.FontSizeMin, LoaderHudConfig.FontSizeMax, "{0:0}");
            }

            AddHudSlider(hud.Scale, "hud.option.scale", "hud.desc.scale", displayName,
                language, category, LoaderHudConfig.ScaleMin, LoaderHudConfig.ScaleMax, "{0:0.00}");
            AddHudSlider(hud.Opacity, "hud.option.opacity", "hud.desc.opacity", displayName,
                language, category, 0f, 1f, "{0:0.00}");
            if (!isVelocityIcon)
            {
                AddHudSlider(hud.BackgroundOpacity, "hud.option.backgroundOpacity", "hud.desc.backgroundOpacity",
                    displayName, language, category, 0f, 1f, "{0:0.00}");
            }
            AddHudSlider(hud.AnchorX, "hud.option.anchorX", "hud.desc.anchorX", displayName,
                language, category, LoaderHudConfig.AnchorMin, LoaderHudConfig.AnchorMax, "{0:0.00}");
            AddHudSlider(hud.AnchorY, "hud.option.anchorY", "hud.desc.anchorY", displayName,
                language, category, LoaderHudConfig.AnchorMin, LoaderHudConfig.AnchorMax, "{0:0.00}");
            AddHudSlider(hud.OffsetX, "hud.option.offsetX", "hud.desc.offsetX", displayName,
                language, category, LoaderHudConfig.OffsetMin, LoaderHudConfig.OffsetMax, "{0:0.000}");
            AddHudSlider(hud.OffsetY, "hud.option.offsetY", "hud.desc.offsetY", displayName,
                language, category, LoaderHudConfig.OffsetMin, LoaderHudConfig.OffsetMax, "{0:0.000}");
        }
    }

    private static void AddHudSlider(
        ConfigEntry<float> entry, string optionKey, string descriptionKey, string displayName,
        LabelLanguage language, string category, float min, float max, string format)
    {
        ModSettingsManager.AddOption(new SliderOption(entry, new SliderConfig
        {
            name = LabelTable.Format(optionKey, language, displayName),
            description = LabelTable.Format(descriptionKey, language, displayName, entry.DefaultValue),
            category = category,
            min = min,
            max = max,
            FormatString = format,
        }));
    }
}
