// Loader HUD 统一元素组件。
//
// 视觉基线（只读反编译核对，不拷贝素材/代码）：
// - C:/Users/Wray/AppData/Local/Temp/lr/out/LoaderRuler.decompiled.cs:835-854 的 TextHud 把
//   HGTextMeshProUGUI 挂到 hud.mainContainer 下，anchorMin/Max=0、anchoredPosition 为绝对像素、
//   fontSizeMin=6、outlineWidth=0.1、无布局。裸文字 + 绝对像素 + 无排版就是“丑”的来源。
// - 本文件因此改用：归一化锚点 + 相对偏移（随分辨率/UI 缩放自适应，不写绝对像素默认值）、
//   标签小字 + 数值大字分层、<mspace> 等宽数字、统一 HG 字体与 0.1 描边、可选半透明细底。
// - 字号用 LoaderHudMath.DampToward 做帧率无关阻尼，避免速度/记录变化时跳字号。

using System;
using BepInEx.Logging;
using RoR2;
using RoR2.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoR2LatencyMod;

/// <summary>Per-frame data handed to one HUD. No allocation; game types stay on this side.</summary>
public readonly struct LoaderHudContext
{
    public readonly HUD Hud;
    public readonly CharacterBody Body;
    public readonly float DeltaSeconds;

    public LoaderHudContext(HUD hud, CharacterBody body, float deltaSeconds)
    {
        Hud = hud;
        Body = body;
        DeltaSeconds = deltaSeconds;
    }
}

/// <summary>
/// What one HUD wants shown this frame. <see cref="None"/> (empty Value) hides it.
/// Provider stubs return None until their follow-up data task is wired.
/// </summary>
public readonly struct LoaderHudReadout
{
    public static readonly LoaderHudReadout None = new LoaderHudReadout(null, null, default, false, 1f);

    public readonly string Label;
    public readonly string Value;
    public readonly Color ValueColor;
    public readonly bool HasValueColor;
    public readonly float FontSizeScale;

    public LoaderHudReadout(
        string label, string value, Color valueColor = default, bool hasValueColor = false, float fontSizeScale = 1f)
    {
        Label = label;
        Value = value;
        ValueColor = valueColor;
        HasValueColor = hasValueColor;
        FontSizeScale = fontSizeScale > 0f ? fontSizeScale : 1f;
    }
}

/// <summary>
/// Base for the six Loader HUD elements: config, lifecycle and a single independent
/// try/catch boundary. Concrete HUDs only override <see cref="Read"/> (stubs return None).
/// </summary>
public abstract class LoaderHudElement : MonoBehaviour
{
    private LoaderHudText view;

    internal bool Faulted { get; private set; }

    protected LoaderHudConfig Config { get; private set; }

    internal void Bind(LoaderHudConfig config)
    {
        Config = config;
        view = gameObject.AddComponent<LoaderHudText>();
        view.Bind(config);
    }

    internal void Tick(in LoaderHudContext context, bool bodyVisible)
    {
        if (Faulted || view == null)
        {
            return;
        }

        if (!Config.Enabled.Value)
        {
            OnHidden();
            view.Hide();
            return;
        }

        if (!bodyVisible)
        {
            OnHidden();
        }

        LoaderHudReadout readout = bodyVisible ? Read(in context) : LoaderHudReadout.None;
        view.Render(readout, context.DeltaSeconds);
    }

    /// <summary>
    /// Called every hidden frame. HUDs that keep per-frame motion samples must drop them here,
    /// otherwise the first visible frame divides a stale position delta by one frame.
    /// </summary>
    protected virtual void OnHidden()
    {
    }

    /// <summary>Circuit breaker: a throwing HUD disables only itself; host keeps the others running.</summary>
    internal void Fault(Exception exception, ManualLogSource log)
    {
        if (Faulted)
        {
            return;
        }

        Faulted = true;
        log?.LogWarning(
            $"[LoaderHud] {GetType().Name} 已熔断（只禁用自身，其它 HUD 与补丁继续）：" +
            $"{exception.GetType().Name}: {exception.Message}");
        view?.Hide();
        gameObject.SetActive(false);
    }

    /// <summary>TODO follow-up task: read real game data; stubs return <see cref="LoaderHudReadout.None"/>.</summary>
    protected abstract LoaderHudReadout Read(in LoaderHudContext context);
}

/// <summary>
/// Unified HUD element: HGTextMeshProUGUI + optional thin translucent Image backing,
/// small label over large value, monospaced digits, one outline style, damped font size.
/// Lives on the element GameObject; its RectTransform is the anchor unit.
/// </summary>
public sealed class LoaderHudText : MonoBehaviour
{
    private const float FontDampingRate = 12f;
    private const float Padding = 4f;
    private const string MonospaceTagOpen = "<mspace=0.6em>";

    private LoaderHudConfig config;
    private RectTransform rect;
    private HGTextMeshProUGUI text;
    private Image background;

    private float smoothedFontSize;
    private float appliedFontSize = -1f;
    private string lastContent;
    private string lastLabel;
    private string lastValue;
    private Color lastValueColor;
    private bool lastHasValueColor;
    private float resizeCountdown;
    private bool resizePending;
    private Vector2 appliedAnchor = new Vector2(float.NaN, float.NaN);
    private Vector2 appliedParentSize = new Vector2(float.NaN, float.NaN);
    private float appliedOffsetX = float.NaN;
    private float appliedOffsetY = float.NaN;
    private float appliedScale = float.NaN;
    private Color appliedColor = new Color(0f, 0f, 0f, 0f);
    private Color appliedBackgroundColor = new Color(0f, 0f, 0f, 0f);
    private bool visible;

    internal void Bind(LoaderHudConfig hudConfig)
    {
        config = hudConfig ?? throw new ArgumentNullException(nameof(hudConfig));
        rect = (RectTransform)transform;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = Vector2.zero;

        var textObject = new GameObject("Text", typeof(RectTransform));
        var textRect = (RectTransform)textObject.transform;
        textRect.SetParent(transform, false);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        text = textObject.AddComponent<HGTextMeshProUGUI>();
        text.useLanguageDefaultFont = true;
        if (text.font == null && TMP_Settings.defaultFontAsset != null)
        {
            text.font = TMP_Settings.defaultFontAsset;
        }

        text.raycastTarget = false;
        text.richText = true;
        text.enableWordWrapping = false;
        text.alignment = TextAlignmentOptions.Center;
        text.outlineColor = new Color32(0, 0, 0, 180);
        text.outlineWidth = 0.1f;
        text.fontSize = config.FontSize.Value;
        text.enabled = false;
        smoothedFontSize = config.FontSize.Value;

        // Thin backing on the element itself; children draw on top. Alpha 0 means "off".
        background = gameObject.AddComponent<Image>();
        background.raycastTarget = false;
        background.enabled = false;
    }

    internal void Render(LoaderHudReadout readout, float deltaSeconds)
    {
        if (config == null || !config.Enabled.Value || string.IsNullOrEmpty(readout.Value))
        {
            Hide();
            return;
        }

        Show();
        ApplyPlacement();
        ApplyColor();

        float targetSize = config.FontSize.Value * readout.FontSizeScale;
        smoothedFontSize = (float)LoaderHudMath.DampToward(
            smoothedFontSize, targetSize, FontDampingRate, deltaSeconds);
        bool sizeChanged = appliedFontSize < 0f || Mathf.Abs(smoothedFontSize - appliedFontSize) > 0.05f;
        if (sizeChanged)
        {
            appliedFontSize = smoothedFontSize;
            text.fontSize = smoothedFontSize;
        }

        // Providers cache their formatted strings, so unchanged frames only compare references
        // and skip BuildContent/ColorUtility allocations entirely.
        bool contentChanged =
            !ReferenceEquals(readout.Value, lastValue) ||
            !string.Equals(readout.Label, lastLabel, StringComparison.Ordinal) ||
            readout.HasValueColor != lastHasValueColor ||
            (readout.HasValueColor && readout.ValueColor != lastValueColor);
        bool textChanged = false;
        if (contentChanged)
        {
            lastValue = readout.Value;
            lastLabel = readout.Label;
            lastHasValueColor = readout.HasValueColor;
            lastValueColor = readout.ValueColor;
            string content = BuildContent(readout);
            textChanged = !string.Equals(content, lastContent, StringComparison.Ordinal);
            if (textChanged)
            {
                lastContent = content;
                text.text = content;
            }
        }

        // ForceMeshUpdate only feeds the background size; 10 Hz is enough and avoids a
        // per-frame TMP layout pass while the damped font size is moving.
        if (sizeChanged || textChanged)
        {
            resizePending = true;
        }

        resizeCountdown -= deltaSeconds;
        if (resizePending && resizeCountdown <= 0f)
        {
            ResizeToContent();
            resizePending = false;
            resizeCountdown = 0.1f;
        }
    }

    internal void Hide()
    {
        visible = false;
        if (text != null)
        {
            text.enabled = false;
        }

        if (background != null)
        {
            background.enabled = false;
        }
    }

    private void Show()
    {
        if (visible)
        {
            return;
        }

        visible = true;
        text.enabled = true;
    }

    private void ApplyPlacement()
    {
        var parent = rect.parent as RectTransform;
        if (parent == null)
        {
            return;
        }

        var anchor = new Vector2(config.AnchorX.Value, config.AnchorY.Value);
        if (anchor != appliedAnchor)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            appliedAnchor = anchor;
        }

        Vector2 parentSize = parent.rect.size;
        float offsetX = config.OffsetX.Value;
        float offsetY = config.OffsetY.Value;
        if (parentSize != appliedParentSize || offsetX != appliedOffsetX || offsetY != appliedOffsetY)
        {
            // t1 AnchorPixelOffset is relative * size * uiScale. The parent rect is already in
            // canvas-local units (CanvasScaler applied), so uiScale=1 gives a resolution- and
            // UI-scale-correct anchoredPosition without any absolute-pixel default.
            Vec2 offset = LoaderHudMath.AnchorPixelOffset(
                offsetX, offsetY, parentSize.x, parentSize.y, 1.0);
            rect.anchoredPosition = new Vector2((float)offset.X, (float)offset.Y);
            appliedParentSize = parentSize;
            appliedOffsetX = offsetX;
            appliedOffsetY = offsetY;
        }

        float scale = config.Scale.Value;
        if (scale != appliedScale)
        {
            appliedScale = scale;
            rect.localScale = new Vector3(scale, scale, 1f);
        }
    }

    private void ApplyColor()
    {
        Color configured = config.TextColor.Value;
        float alpha = Mathf.Clamp01(configured.a * config.Opacity.Value);
        var wanted = new Color(configured.r, configured.g, configured.b, alpha);
        if (wanted != appliedColor)
        {
            appliedColor = wanted;
            text.color = wanted;
        }

        float backgroundAlpha = Mathf.Clamp01(config.BackgroundOpacity.Value * alpha);
        bool backgroundOn = backgroundAlpha > 0.001f;
        background.enabled = backgroundOn;
        var wantedBackground = new Color(0f, 0f, 0f, backgroundAlpha);
        if (backgroundOn && wantedBackground != appliedBackgroundColor)
        {
            appliedBackgroundColor = wantedBackground;
            background.color = wantedBackground;
        }
    }

    private string BuildContent(LoaderHudReadout readout)
    {
        string value = MonospaceTagOpen + readout.Value + "</mspace>";
        if (readout.HasValueColor)
        {
            value = "<color=#" + ColorUtility.ToHtmlStringRGBA(readout.ValueColor) + ">" + value + "</color>";
        }

        if (string.IsNullOrEmpty(readout.Label))
        {
            return value;
        }

        // Small dim label above the large value; keeps one text object per HUD.
        return "<size=60%><alpha=#AA>" + readout.Label + "</alpha></size>\n" + value;
    }

    private void ResizeToContent()
    {
        text.ForceMeshUpdate();
        rect.sizeDelta = new Vector2(
            Mathf.Ceil(text.preferredWidth + Padding * 2f),
            Mathf.Ceil(text.preferredHeight + Padding * 2f));
    }
}
