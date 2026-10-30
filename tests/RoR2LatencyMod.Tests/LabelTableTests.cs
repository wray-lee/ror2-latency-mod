using System;
using System.Reflection;
using RoR2LatencyMod;
using Xunit;

namespace RoR2LatencyMod.Tests;

public sealed class LabelTableTests
{
    [Fact]
    public void EveryKeyHasNonEmptyChineseAndEnglishText()
    {
        foreach (var entry in LabelTable.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Zh), entry.Key + " has no zh text");
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.En), entry.Key + " has no en text");
        }

        Assert.True(LabelTable.All.Count >= 70, "label table lost entries: " + LabelTable.All.Count);
    }

    [Fact]
    public void GetReturnsTheRequestedLanguage()
    {
        foreach (var entry in LabelTable.All)
        {
            Assert.Equal(entry.Value.Zh, LabelTable.Get(entry.Key, LabelLanguage.Chinese));
            Assert.Equal(entry.Value.En, LabelTable.Get(entry.Key, LabelLanguage.English));
        }
    }

    [Theory]
    // zh* plus a CJK-capable font -> Chinese, including case and region variants.
    [InlineData("zh", true, LabelLanguage.Chinese)]
    [InlineData("zh-CN", true, LabelLanguage.Chinese)]
    [InlineData("ZH-hans", true, LabelLanguage.Chinese)]
    // Any other language -> English even when the font has CJK glyphs.
    [InlineData("en", true, LabelLanguage.English)]
    [InlineData("en-US", true, LabelLanguage.English)]
    [InlineData("ja", true, LabelLanguage.English)]
    // No CJK glyphs -> English even for Chinese.
    [InlineData("zh-CN", false, LabelLanguage.English)]
    [InlineData("zh", false, LabelLanguage.English)]
    // Missing/empty language codes cannot prove Chinese support -> English.
    [InlineData("", true, LabelLanguage.English)]
    [InlineData(null, true, LabelLanguage.English)]
    public void SelectFallsBackToEnglishUnlessChineseAndCjkCapable(
        string languageCode, bool fontSupportsCjk, LabelLanguage expected)
    {
        Assert.Equal(expected, LabelTable.Select(languageCode, fontSupportsCjk));
    }

    [Fact]
    public void HudLabelsAndDisplayNamesExist()
    {
        string[] keys =
        {
            "hud.label.grappleDuration",
            "hud.label.grappleLength",
            "hud.label.speed",
            "hud.label.aim",
            "hud.label.punchSpeed",
            "hud.label.grappleEnergy",
            "hud.name.grappleDuration",
            "hud.name.grappleLength",
            "hud.name.speedText",
            "hud.name.punchRecord",
            "hud.name.velocityIcon",
            "hud.name.grappleEnergy",
        };

        foreach (string key in keys)
        {
            Assert.True(LabelTable.All.ContainsKey(key), key);
        }
    }

    [Fact]
    public void EveryRooOptionHasANameAndDescription()
    {
        string[] prefixes =
        {
            "patch.LoaderHookPrediction",
            "patch.LoaderFlyTimeout",
            "patch.LoaderRulerEquivalent",
            "patch.LoaderCrosshair",
            "patch.LoaderAimLead",
            "patch.AimLeadStrength",
            "patch.AimLeadMaxSeconds",
            "patch.FlyTimeoutMargin",
            "patch.FlyTimeoutExtraMaxSeconds",
            "patch.LoaderHookFallbackMaxSeconds",
            "patch.LoaderReturnSpeedMultiplier",
            "patch.EnforcerFeedback",
            "patch.FeedbackFallbackTimeout",
            "patch.FeedbackFallbackMaxSeconds",
            "patch.InitialLatencyCapSeconds",
            "patch.EnforcerStartLead",
            "patch.StartLeadCapSeconds",
            "patch.StartLeadMaxMeters",
            "patch.EnforcerDashLengthMultiplier",
            "patch.EnforcerExitSmoothing",
            "patch.EnforcerGhostDash",
            "patch.EnforcerLocalEnd",
            "patch.ExitSmoothSeconds",
        };

        foreach (string prefix in prefixes)
        {
            Assert.True(LabelTable.All.ContainsKey(prefix + ".name"), prefix + ".name");
            Assert.True(LabelTable.All.ContainsKey(prefix + ".desc"), prefix + ".desc");
        }
    }

    [Fact]
    public void FontProbeSampleCoversEveryNonAsciiGlyphUsedByChineseLabels()
    {
        // The probe must reject a font missing any glyph the Chinese UI can draw, including
        // full-width punctuation, not just Han characters (LabelLanguageProbe.BuildCjkSample).
        var field = typeof(LabelLanguageProbe).GetField(
            "CjkSample", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        string sample = Assert.IsType<string>(field.GetValue(null));

        foreach (char c in "、。，：；（）")
        {
            Assert.Contains(c, sample);
        }
    }
}
