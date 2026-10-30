using System;
using System.Collections.Generic;
using System.Globalization;
using RoR2;
using RoR2.UI;
using TMPro;

namespace RoR2LatencyMod;

/// <summary>
/// Runtime half of the label fallback. BepInEx loads plugins before RoR2 initializes its
/// language, so startup-time strings use the OS UI culture when the game code is still empty;
/// HUD/RoO text resolves the real game language and probes the actual HG/TMP font.
/// </summary>
public static class LabelLanguageProbe
{
    private static readonly string CjkSample = BuildCjkSample();
    private static TMP_FontAsset cachedFont;
    private static bool? fontSupportsCjk;

    /// <summary>Config-file descriptions, written during Plugin.Awake before Language.Init.</summary>
    public static LabelLanguage Startup => Resolve(probeFont: false);

    /// <summary>HUD text and RoO entries, resolved after the game language is initialized.</summary>
    public static LabelLanguage Current => Resolve(probeFont: true);

    private static LabelLanguage Resolve(bool probeFont)
    {
        string code = Language.currentLanguageName;
        if (string.IsNullOrEmpty(code))
        {
            code = CultureInfo.CurrentUICulture.Name;
        }

        return LabelTable.Select(code, !probeFont || FontSupportsCjk());
    }

    private static bool FontSupportsCjk()
    {
        try
        {
            TMP_FontAsset font = HGTextMeshProUGUI.defaultLanguageFont;
            // A null asset means "not loaded yet", not "missing glyphs": do not force English
            // before the game has supplied the font. HasCharacters false/throw does force it.
            if (font == null)
            {
                return true;
            }

            // defaultLanguageFont is replaced when the game language changes; key the cache on
            // the asset so a Chinese font picked at runtime is actually probed.
            if (fontSupportsCjk.HasValue && ReferenceEquals(font, cachedFont))
            {
                return fontSupportsCjk.Value;
            }

            cachedFont = font;
            fontSupportsCjk = font.HasCharacters(CjkSample);
            return fontSupportsCjk.Value;
        }
        catch
        {
            fontSupportsCjk = false;
            return false;
        }
    }

    // Every CJK character the label table can draw, so one missing glyph sends all labels
    // to English instead of showing a half-tofu UI.
    private static string BuildCjkSample()
    {
        var characters = new HashSet<char>();
        foreach (var entry in LabelTable.All)
        {
            foreach (char character in entry.Value.Zh)
            {
                // Probe every non-ASCII glyph the Chinese labels can draw, including full-width
                // punctuation; Han-only sampling let a font with no 、。（） pass the probe.
                if (character > '\u007F')
                {
                    characters.Add(character);
                }
            }
        }

        var sample = new char[characters.Count];
        characters.CopyTo(sample);
        return new string(sample);
    }
}
