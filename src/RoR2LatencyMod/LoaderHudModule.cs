// Loader HUD 模块：挂点、生命周期、观战/死亡、熔断与零开销软开关。
//
// 反编译核对（F:/ror2-decomp/RoR2src，只读；行号为当前反编译版本）：
// - RoR2.UI/HUD.cs:37-39  mainContainer 是“包含全部 UI 的直接子物体”，本模块把 LoaderHudHost
//   作为其子物体挂载；HUD.cs:173-181 mainContainerCanvas 也只从该物体取 Canvas。
// - RoR2.UI/HUD.cs:330-348  Awake 初始化 HUD 字段；postfix 在 mainContainer 就绪后挂宿主。
// - RoR2.UI/HUD.cs:287-294  OnEnable / :296-302 OnDisable：HUD 被停用时子物体一并失活，
//   宿主 Update 自然停止；HUD 重新启用时恢复，无需额外状态。
// - RoR2.UI/HUD.cs:350-358  OnDestroy 清 target 并注销 UI；Unity 随 HUD 销毁其全部子物体，
//   宿主（以及六个 HUD 元素）随之销毁，不残留。原版“hud elements persisting”是挂在
//   mainContainer 之外/静态引用未清导致的；这里不使用 HUD 之外的父节点，也不保留静态元素引用。
// - RoR2.UI/HUD.cs:108-129 targetBodyObject private set 在目标变化时触发 discovered/lost；
//   :304-310 OnTargetMasterDiscovered、:312-318 OnTargetMasterLost、:320-323 body
//   destroyed、:325-328 body changed 都会重设 targetBodyObject。
//   RoR2/CameraRigController.cs:534-538 在 LateUpdate 把 HUD.targetMaster 设为当前相机目标的
//   master（观战换人即换 targetMaster），所以宿主每帧读 HUD.targetBodyObject 即可覆盖
//   “观战 Loader”，无需自己的观战订阅。
// - RoR2.UI/HUD.cs:494-498 Update 比较 previousTargetBodyObject 后才发
//   onHudTargetChangedGlobal；轮询 targetBodyObject 比该事件更早、且不受 HUD 更新顺序影响。
// - RoR2.UI/HGTextMeshProUGUI.cs:28-36 Awake 自动套用游戏当前语言字体（defaultLanguageFont）。
//
// 软开关：任何 HUD 全关时 Initialize 不装 Harmony patch、不建宿主，启动/逐帧零开销。
// 因此从“全关”改为“开”需要重启（配置/RoO 均已标注 restartRequired）；运行中把最后一项关掉，
// 宿主下一帧自毁，之后同样零开销。

using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using RoR2;
using RoR2.UI;
using UnityEngine;

namespace RoR2LatencyMod;

public static class LoaderHudModule
{
    private const string HostObjectName = "LoaderHudHost";
    private const string LoaderBodyName = "LoaderBody";

    private static ManualLogSource log;
    private static bool loaderBodyIndexResolved;
    private static BodyIndex loaderBodyIndex;

    /// <summary>
    /// Called from Plugin.Awake after PatchConfig.Bind. Installs only the HUD.Awake postfix;
    /// each HUD element keeps its own try/catch, so one bad HUD cannot affect patches or peers.
    /// </summary>
    public static void Initialize(ManualLogSource logger)
    {
        log = logger;
        try
        {
            logger?.LogInfo(DescribeConfig());

            if (!AnyEnabled())
            {
                logger?.LogInfo("[LoaderHud] 全部 HUD 关闭：不安装 HUD 钩子、不建宿主（零开销）。");
                return;
            }

            var hudAwake = AccessTools.Method(typeof(HUD), "Awake");
            if (hudAwake == null)
            {
                throw new MissingMemberException("RoR2.UI.HUD.Awake not found");
            }

            var harmony = new Harmony(Plugin.PluginGuid + ".LoaderHud");
            harmony.Patch(hudAwake, postfix: new HarmonyMethod(
                typeof(LoaderHudModule), nameof(HudAwakePostfix)));
            logger?.LogInfo("[LoaderHud] 已安装 HUD.Awake postfix，宿主挂到 HUD.mainContainer（随 HUD 销毁）。");
        }
        catch (Exception ex)
        {
            // HUD 是显示功能：任何初始化失败只关闭 HUD，绝不影响延迟补丁。
            logger?.LogWarning(
                $"[LoaderHud] HUD 模块停用（补丁不受影响）：" +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool AnyEnabled()
    {
        IReadOnlyList<LoaderHudConfig> huds = PatchConfig.LoaderHuds;
        for (int i = 0; i < huds.Count; i++)
        {
            LoaderHudConfig hud = huds[i];
            if (hud != null && hud.Enabled.Value)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsLoaderBody(CharacterBody body)
    {
        if (body == null)
        {
            return false;
        }

        if (!loaderBodyIndexResolved)
        {
            loaderBodyIndex = BodyCatalog.FindBodyIndex(LoaderBodyName);
            loaderBodyIndexResolved = loaderBodyIndex != BodyIndex.None;
        }

        return loaderBodyIndexResolved && body.bodyIndex == loaderBodyIndex;
    }

    private static void HudAwakePostfix(HUD __instance)
    {
        GameObject hostObject = null;
        try
        {
            if (__instance == null || __instance.mainContainer == null || !AnyEnabled())
            {
                return;
            }

            Transform parent = __instance.mainContainer.transform;
            if (parent.Find(HostObjectName) != null)
            {
                return;
            }

            hostObject = new GameObject(HostObjectName, typeof(RectTransform));
            var hostRect = (RectTransform)hostObject.transform;
            hostRect.SetParent(parent, false);
            hostRect.anchorMin = Vector2.zero;
            hostRect.anchorMax = Vector2.one;
            hostRect.offsetMin = Vector2.zero;
            hostRect.offsetMax = Vector2.zero;
            hostObject.AddComponent<LoaderHudHost>().Initialize(__instance, log);
        }
        catch (Exception ex)
        {
            // A half-built host must not linger under the HUD.
            if (hostObject != null)
            {
                UnityEngine.Object.Destroy(hostObject);
            }

            log?.LogWarning(
                $"[LoaderHud] HUD 宿主创建失败（该项 HUD 不显示，补丁不受影响）：" +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string DescribeConfig()
    {
        var builder = new StringBuilder("[LoaderHud] 启动汇总：");
        IReadOnlyList<LoaderHudConfig> huds = PatchConfig.LoaderHuds;
        for (int i = 0; i < huds.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(huds[i].DisplayName).Append('=').Append(huds[i].Enabled.Value ? "开" : "关");
        }

        return builder.ToString();
    }
}

/// <summary>
/// One per RoR2.UI.HUD, child of mainContainer. Creates the six elements, resolves the
/// current target (own or spectated Loader body) and isolates every HUD in its own try/catch.
/// </summary>
public sealed class LoaderHudHost : MonoBehaviour
{
    private readonly List<LoaderHudElement> elements = new List<LoaderHudElement>(6);
    private HUD hud;
    private ManualLogSource log;
    private GameObject lastTarget;
    private CharacterBody targetBody;

    internal void Initialize(HUD owner, ManualLogSource logger)
    {
        hud = owner;
        log = logger;
        Build<LoaderHudGrappleDuration>(PatchConfig.HudGrappleDuration);
        Build<LoaderHudGrappleLength>(PatchConfig.HudGrappleLength);
        Build<LoaderHudSpeedText>(PatchConfig.HudSpeedText);
        Build<LoaderHudPunchRecord>(PatchConfig.HudPunchRecord);
        Build<LoaderHudVelocityIcon>(PatchConfig.HudVelocityIcon);
        Build<LoaderHudGrappleEnergy>(PatchConfig.HudGrappleEnergy);

        if (elements.Count == 0)
        {
            log?.LogWarning("[LoaderHud] 六项 HUD 全部创建失败，宿主自毁（补丁不受影响）。");
            Destroy(gameObject);
        }
    }

    private void Build<T>(LoaderHudConfig config) where T : LoaderHudElement
    {
        if (config == null)
        {
            return;
        }

        GameObject elementObject = null;
        try
        {
            elementObject = new GameObject("LoaderHud." + config.Key, typeof(RectTransform));
            elementObject.transform.SetParent(transform, false);
            var element = elementObject.AddComponent<T>();
            element.Bind(config);
            elements.Add(element);
        }
        catch (Exception ex)
        {
            if (elementObject != null)
            {
                Destroy(elementObject);
            }

            log?.LogWarning(
                $"[LoaderHud] {config.DisplayName} 创建失败，只跳过该项：" +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Update()
    {
        try
        {
            // Last HUD turned off at runtime: drop the host so the zero-overhead path returns.
            if (!LoaderHudModule.AnyEnabled())
            {
                Destroy(gameObject);
                return;
            }

            TickElements();
        }
        catch (Exception ex)
        {
            // Host-level failure would otherwise repeat every frame; log once and disable all HUDs.
            log?.LogWarning(
                $"[LoaderHud] HUD 宿主已停用（补丁不受影响）：{ex.GetType().Name}: {ex.Message}");
            Destroy(gameObject);
        }
    }

    private void TickElements()
    {
        if (hud == null)
        {
            Destroy(gameObject);
            return;
        }

        // HUD.cs:304-329: targetBodyObject follows the spectated master/body; death clears it.
        GameObject target = hud.targetBodyObject;
        if (target != lastTarget)
        {
            lastTarget = target;
            targetBody = target != null ? target.GetComponent<CharacterBody>() : null;
        }

        // HUD.cs:582-594 hides mainUIPanel when the camera disallows HUD; mirror that so
        // Loader HUD text never floats over menus/pause. (mainContainer itself stays active.)
        bool hudVisible = hud.mainUIPanel == null || hud.mainUIPanel.activeInHierarchy;
        bool bodyVisible = hudVisible && LoaderHudModule.IsLoaderBody(targetBody);
        if (bodyVisible)
        {
            HealthComponent health = targetBody.healthComponent;
            bodyVisible = health != null && health.alive;
        }

        var context = new LoaderHudContext(hud, targetBody, Time.deltaTime);
        for (int i = 0; i < elements.Count; i++)
        {
            LoaderHudElement element = elements[i];
            if (element == null || element.Faulted)
            {
                continue;
            }

            try
            {
                element.Tick(in context, bodyVisible);
            }
            catch (Exception ex)
            {
                element.Fault(ex, log);
            }
        }
    }

    private void OnDestroy()
    {
        elements.Clear();
        hud = null;
        log = null;
    }
}
