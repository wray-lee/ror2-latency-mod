// 票04 LoaderHookPrediction：Loader 钩爪按下即出现，不再等一个 RTT。
// 做法：在客户端发起投射物的那一刻（ProjectileManager.FireProjectileClient 读取 allowPrediction 之前）
// 把钩爪 prefab 上 ProjectileController.allowPrediction 置 true，走原版 predictionId 交接链
// （ProjectileManager+PredictionManager），不新增网络消息、不改伤害/命中。
// 仅视觉（本票根因修复）：预测钩没有 authority，FlyState 对 !isAuthority 直接 return
// （ProjectileGrappleController.cs:132），但它的 Start 会调用 AssignHookReferenceToBodyStateMachine
// 把 FireHook.hookInstance 抢成预测钩（:461-465 -> :439-446 -> FireHook.cs:61-66），而 FireHook 只在
// `isAuthority && !hookInstance && hadHookInstance` 时退出（FireHook.cs:79），于是动画永不结束、
// 真钩到达也无法回绑。因此预测钩跳过整个 Assign：不设 hookInstance、不把 ropeEndTransform 挂到身体
// （无绳子拉力）；预测钩碰撞事件恒被拒（ProjectileController.CanProcessCollisionEvents:408-423），
// 永不 stick。预测钩只在三条路径销毁（PredictionMath.PredictedHookDrop）：
//   1) 真钩 handoff：ProjectileManager.OnClientProjectileReceived 内交接之后；
//   2) FireHook 退出：FireHook.OnExit 之后；
//   3) 超时：max(2×RTT, 0.5s)，上限 FeedbackFallbackMaxSeconds（PredictionMath.HoldTimeout）。
// 不双毁 ghost 的依据：handoff 先把预测钩的 ghost 移交给真钩并把 predicted.ghost 置 null
// （ProjectileManager.cs:183-199），预测钩 OnDestroy -> DisconnectFromGhost 对 ghost==null 直接 return
// （ProjectileController.cs:277-281）；ghost 是 EffectManager/Instantiate 出来的独立物体，不是预测钩的
// 子物体。原版本来就在服务器销毁真钩后经 ReleasePredictionId（ProjectileManager.cs:202-211, :614-624）
// 销毁同一个预测物体（字典引用有 Unity 空判），本票只是把同一销毁路径提前到 handoff。
// 反编译依据：ProjectileController.cs:169（Awake 收集 handoff）、:196/:214（预测/真钩 ghost）、
// ProjectileManager.cs:446（allowPrediction 判定）、:187-200（predictionId 交接）、:579-593（客户端权威生成）。
// 覆盖默认 LoaderHook 与副技能变体 LoaderYankHook（真实房间日志里用户实际发射的是后者；FireYankHook 只是
// 继承 FireHook 的空子类，投射物 prefab 的差异只在 yankMassLimit/ownerHookStateType 等序列化字段）。
// 仅当 prefab 支持 localPlayerAuthority 时才启用；否则只跳过该 prefab 并写日志，不留半成品。

using System;
using System.Collections.Generic;
using EntityStates.Loader;
using HarmonyLib;
using RoR2;
using RoR2.Projectile;
using UnityEngine;
using UnityEngine.Networking;

namespace RoR2LatencyMod;

public sealed class LoaderHookPredictionPatch : SkillPatch
{
    private static readonly string[] HookPrefabNames = { "LoaderHook", "LoaderYankHook" };

    private static LoaderHookPredictionPatch instance;

    private readonly List<PendingPrediction> pending = new List<PendingPrediction>();
    private readonly Func<float> feedbackFallbackMaxSeconds;

    private List<HookPrefab> hookPrefabs;

    private sealed class HookPrefab
    {
        public string Name;
        public GameObject Prefab;
        public ProjectileController Controller;
        public int CatalogIndex = -1;
        public bool Captured;
        public bool Unsupported;
        public bool OriginalAllowPrediction;
    }

    /// <summary>A live client-only predicted hook copy; never has authority, never drives the body.</summary>
    private sealed class PendingPrediction
    {
        public ProjectileController Controller;
        public GameObject Owner;
        public ushort PredictionId;
        public float CreatedTime;
        public string Name;
    }

    public LoaderHookPredictionPatch(
        Func<bool> isEnabled,
        DiagLog diag = null,
        LatencyEstimator latency = null,
        Func<float> feedbackFallbackMaxSeconds = null)
        : base("LoaderHookPrediction", PatchSide.Client, isEnabled, diag, latency)
    {
        this.feedbackFallbackMaxSeconds = feedbackFallbackMaxSeconds;
    }

    protected override void Install()
    {
        var managerType = AccessTools.TypeByName("RoR2.Projectile.ProjectileManager");
        if (managerType == null)
        {
            throw new MissingMemberException("RoR2.Projectile.ProjectileManager not found");
        }

        var fireClient = AccessTools.Method(
            managerType,
            "FireProjectileClient",
            new[] { typeof(FireProjectileInfo), typeof(NetworkClient), typeof(double) });
        var clientReceived = AccessTools.Method(
            managerType,
            "OnClientProjectileReceived",
            new[] { typeof(ProjectileController) });
        var fireHookOnEnter = AccessTools.Method(typeof(FireHook), "OnEnter", Type.EmptyTypes);
        // The flipped prefab must be vanilla again before the next run may be hosted in this
        // process: the host's own projectile (server-owned, allowPrediction=true) would skip
        // ProjectileController.Start's ghost creation and stay invisible. Run.Awake is
        // non-virtual, so Unity calls it for every run type.
        var runAwake = AccessTools.Method(typeof(Run), "Awake", Type.EmptyTypes);
        var grappleType = AccessTools.TypeByName("RoR2.Projectile.ProjectileGrappleController");
        var assignHookReference = grappleType == null
            ? null
            : AccessTools.Method(grappleType, "AssignHookReferenceToBodyStateMachine", Type.EmptyTypes);
        var grappleFixedUpdate = grappleType == null
            ? null
            : AccessTools.Method(grappleType, "FixedUpdate", Type.EmptyTypes);
        var projectileStart = AccessTools.Method(typeof(ProjectileController), "Start", Type.EmptyTypes);
        var fireHookOnExit = AccessTools.Method(typeof(FireHook), "OnExit", Type.EmptyTypes);
        if (fireClient == null || clientReceived == null || fireHookOnEnter == null || runAwake == null ||
            assignHookReference == null || grappleFixedUpdate == null ||
            projectileStart == null || fireHookOnExit == null)
        {
            throw new MissingMemberException(
                "LoaderHookPrediction members missing: " +
                $"FireProjectileClient={fireClient != null}, OnClientProjectileReceived={clientReceived != null}, " +
                $"FireHook.OnEnter={fireHookOnEnter != null}, Run.Awake={runAwake != null}, " +
                $"AssignHookReferenceToBodyStateMachine={assignHookReference != null}, " +
                $"ProjectileGrappleController.FixedUpdate={grappleFixedUpdate != null}, " +
                $"ProjectileController.Start={projectileStart != null}, " +
                $"FireHook.OnExit={fireHookOnExit != null}");
        }

        instance = this;
        var harmony = new Harmony(Plugin.PluginGuid + "." + Name);
        harmony.Patch(
            fireClient,
            prefix: new HarmonyMethod(typeof(LoaderHookPredictionPatch), nameof(FireProjectileClientPrefix)));
        harmony.Patch(
            clientReceived,
            postfix: new HarmonyMethod(typeof(LoaderHookPredictionPatch), nameof(OnClientProjectileReceivedPostfix)));
        harmony.Patch(
            fireHookOnEnter,
            postfix: new HarmonyMethod(typeof(LoaderHookPredictionPatch), nameof(FireHookOnEnterPostfix)));
        harmony.Patch(
            runAwake,
            prefix: new HarmonyMethod(typeof(LoaderHookPredictionPatch), nameof(RunAwakePrefix)));
        harmony.Patch(
            assignHookReference,
            prefix: new HarmonyMethod(
                typeof(LoaderHookPredictionPatch), nameof(SkipPredictedHookAssignmentPrefix)));
        harmony.Patch(
            projectileStart,
            postfix: new HarmonyMethod(typeof(LoaderHookPredictionPatch), nameof(TrackPredictedHookPostfix)));
        harmony.Patch(
            grappleFixedUpdate,
            postfix: new HarmonyMethod(typeof(LoaderHookPredictionPatch), nameof(DropExpiredPredictedHookPostfix)));
        harmony.Patch(
            fireHookOnExit,
            postfix: new HarmonyMethod(
                typeof(LoaderHookPredictionPatch), nameof(DropPredictedHookOnFireHookExitPostfix)));

        Diag?.Info(
            "LoaderHookPrediction: installed; allowPrediction is enabled on the first client shot (before " +
            "ProjectileManager reads it) and restored at every Run.Awake so a later host run stays vanilla. " +
            "Predicted hooks are visual-only (hookInstance/rope untouched) and are dropped on handoff, on " +
            "FireHook exit, or at max(2 x RTT, 0.5 s) capped by FeedbackFallbackMaxSeconds.");
    }

    private static void RunAwakePrefix(object __instance)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        // No Guard(): a tripped patch must still clear a previously flipped prefab for this run.
        self.RestoreOriginalIfNeeded();
        self.hookPrefabs = null; // catalog prefabs may be rebuilt next run
        self.pending.Clear(); // last run's predicted copies died with it
    }

    private static void FireProjectileClientPrefix(FireProjectileInfo fireProjectileInfo)
    {
        var self = instance;
        if (self == null)
        {
            return;
        }

        var firedPrefab = fireProjectileInfo.projectilePrefab;
        if (firedPrefab == null)
        {
            return;
        }

        // Restore even while disabled/tripped: Guard() short-circuits after a trip.
        if (!self.ShouldRun())
        {
            self.RestoreOriginalIfNeeded();
            return;
        }

        // Cheap gate before allocating a Guard closure: this runs for every client projectile.
        if (self.hookPrefabs != null)
        {
            if (self.FindHookPrefab(firedPrefab) == null)
            {
                return;
            }
        }
        else if (firedPrefab.GetComponent<ProjectileGrappleController>() == null)
        {
            return;
        }

        self.Guard(() => self.Apply(firedPrefab));
    }

    private void Apply(GameObject firedPrefab)
    {
        if (!ShouldRun())
        {
            RestoreOriginalIfNeeded();
            return;
        }

        if (hookPrefabs == null)
        {
            ResolveHookPrefabs();
        }

        var entry = FindHookPrefab(firedPrefab);
        if (entry == null)
        {
            return; // Not a Loader hook prefab; leave vanilla.
        }

        if (!entry.Captured)
        {
            Capture(entry);
        }

        if (entry.Unsupported || entry.Controller == null)
        {
            return;
        }

        if (!entry.Controller.allowPrediction)
        {
            entry.Controller.allowPrediction = true;
            Diag?.Info(
                $"LoaderHookPrediction: allowPrediction=true on '{entry.Name}' " +
                $"(was {entry.OriginalAllowPrediction}); predicted hook enabled.");
        }
    }

    private void ResolveHookPrefabs()
    {
        var resolved = new List<HookPrefab>(HookPrefabNames.Length);
        foreach (string prefabName in HookPrefabNames)
        {
            int index = ProjectileCatalog.FindProjectileIndex(prefabName);
            var prefab = index >= 0 ? ProjectileCatalog.GetProjectilePrefab(index) : null;
            if (prefab == null)
            {
                Diag?.Warning(
                    $"LoaderHookPrediction: ProjectileCatalog has no projectile named '{prefabName}' " +
                    $"(index={index}); that variant stays vanilla.");
                continue;
            }

            resolved.Add(new HookPrefab
            {
                Name = prefabName,
                Prefab = prefab,
                CatalogIndex = index,
            });
        }

        if (resolved.Count == 0)
        {
            Trip(
                "ProjectileCatalog has neither 'LoaderHook' nor 'LoaderYankHook'; " +
                "cannot enable Loader hook prediction, staying vanilla.");
            return;
        }

        hookPrefabs = resolved;
        var names = new List<string>(resolved.Count);
        foreach (var entry in resolved)
        {
            names.Add($"{entry.Name}@{entry.CatalogIndex}");
        }

        Diag?.Info($"LoaderHookPrediction: resolved hook prefabs: {string.Join(", ", names)}.");
    }

    private HookPrefab FindHookPrefab(GameObject prefab)
    {
        if (hookPrefabs == null || prefab == null)
        {
            return null;
        }

        foreach (var entry in hookPrefabs)
        {
            if (entry.Prefab == prefab)
            {
                return entry;
            }
        }

        return null;
    }

    private HookPrefab FindHookPrefab(int catalogIndex)
    {
        if (hookPrefabs == null)
        {
            return null;
        }

        foreach (var entry in hookPrefabs)
        {
            if (entry.CatalogIndex == catalogIndex)
            {
                return entry;
            }
        }

        return null;
    }

    private void Capture(HookPrefab entry)
    {
        entry.Captured = true;
        entry.Controller = entry.Prefab.GetComponent<ProjectileController>();
        if (entry.Controller == null)
        {
            entry.Unsupported = true;
            Diag?.Warning($"LoaderHookPrediction: prefab '{entry.Name}' has no ProjectileController; staying vanilla.");
            return;
        }

        var identity = entry.Prefab.GetComponent<NetworkIdentity>();
        if (identity == null)
        {
            entry.Unsupported = true;
            Diag?.Warning($"LoaderHookPrediction: prefab '{entry.Name}' has no NetworkIdentity; staying vanilla.");
            return;
        }

        // Without client authority the server cannot hand the authoritative hook over to the
        // local prediction (ProjectileManager.cs:187-200, :586); a predicted hook would linger.
        if (!identity.localPlayerAuthority)
        {
            entry.Unsupported = true;
            Diag?.Warning(
                $"LoaderHookPrediction: prefab '{entry.Name}' has NetworkIdentity.localPlayerAuthority=false; " +
                "vanilla prediction handoff is impossible, staying vanilla for this variant.");
            return;
        }

        entry.OriginalAllowPrediction = entry.Controller.allowPrediction;
        Diag?.Info(
            $"LoaderHookPrediction: prefab '{entry.Name}' is prediction-capable " +
            $"(localPlayerAuthority=true, original allowPrediction={entry.OriginalAllowPrediction}).");
    }

    private void RestoreOriginalIfNeeded()
    {
        if (hookPrefabs == null)
        {
            return;
        }

        foreach (var entry in hookPrefabs)
        {
            try
            {
                if (!entry.Captured || entry.Controller == null ||
                    entry.Controller.allowPrediction == entry.OriginalAllowPrediction)
                {
                    continue;
                }

                entry.Controller.allowPrediction = entry.OriginalAllowPrediction;
                Diag?.Info(
                    $"LoaderHookPrediction: restored allowPrediction={entry.OriginalAllowPrediction} on " +
                    $"'{entry.Name}' (run start, switch off, or trip); the next shot follows vanilla.");
            }
            catch (Exception exception)
            {
                Diag?.Warning($"LoaderHookPrediction: restore failed on '{entry.Name}': {exception.Message}");
            }
        }
    }

    private static void OnClientProjectileReceivedPostfix(ProjectileController projectile)
    {
        var self = instance;
        if (self == null || projectile == null || self.hookPrefabs == null)
        {
            return;
        }

        // Cheap gate before allocating a Guard closure: this runs for every received projectile.
        var entry = self.FindHookPrefab(projectile.catalogIndex);
        if (entry == null || projectile.predictionId == 0 || !projectile.hasAuthority)
        {
            return;
        }

        self.Guard(() => self.LogHandoff(projectile, entry));
        // The handoff just moved the predicted ghost onto this authority projectile (predicted.ghost
        // is null now), so destroying the leftover predicted body cannot touch that ghost.
        self.Guard(() => self.DropPendingOnHandoff(projectile));
    }

    private void LogHandoff(ProjectileController projectile, HookPrefab entry)
    {
        if (!ShouldRun())
        {
            return;
        }

        Diag?.Throttled(
            $"loader-hook-handoff.{entry.Name}",
            $"LoaderHookPrediction: '{entry.Name}' handoff predictionId={projectile.predictionId} " +
            $"isPrediction={projectile.isPrediction} ghostHandedOff={projectile.ghost != null}.");
    }

    /// <summary>Logs the actual prefab the player fired, before/after our flip. Per shot, not per frame.</summary>
    private static void FireHookOnEnterPostfix(FireHook __instance)
    {
        var self = instance;
        if (self == null || !self.ShouldRun() || __instance == null || __instance.outer == null)
        {
            return;
        }

        if (!Util.HasEffectiveAuthority(__instance.outer.networkIdentity))
        {
            return; // Remote players' hook states replicated to us are not ours to diagnose.
        }

        try
        {
            var prefab = __instance.projectilePrefab;
            var entry = self.FindHookPrefab(prefab);
            bool allowPrediction = entry?.Controller != null ? entry.Controller.allowPrediction : false;
            self.Diag?.Info(
                $"LoaderHookPrediction: FireHook enter prefab='{(prefab != null ? prefab.name : "<null>")}' " +
                $"ours={(entry != null ? "yes" : "no")} allowPrediction={allowPrediction} " +
                $"original={(entry != null ? entry.OriginalAllowPrediction.ToString() : "?")}.");
        }
        catch (Exception exception)
        {
            // A diagnostic failure must not break FireHook.OnEnter.
            self.Diag?.Warning(
                $"LoaderHookPrediction: FireHook enter logging skipped ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// Predicted copies never take over the body: skipping the whole assignment leaves
    /// FireHook.hookInstance for the authority hook and keeps the rope off the body (no tension).
    /// </summary>
    private static bool SkipPredictedHookAssignmentPrefix(ProjectileGrappleController __instance)
    {
        if (__instance == null)
        {
            return true;
        }

        var controller = __instance.GetComponent<ProjectileController>();
        return controller == null || !controller.isPrediction;
    }

    private static void TrackPredictedHookPostfix(ProjectileController __instance)
    {
        var self = instance;
        if (self == null || __instance == null || !__instance.isPrediction ||
            __instance.predictionId == 0 || self.hookPrefabs == null)
        {
            return;
        }

        var entry = self.FindHookPrefab(__instance.catalogIndex);
        if (entry == null)
        {
            return;
        }

        self.Guard(() => self.Track(__instance, entry));
    }

    private void Track(ProjectileController controller, HookPrefab entry)
    {
        pending.Add(new PendingPrediction
        {
            Controller = controller,
            Owner = controller.owner,
            PredictionId = controller.predictionId,
            CreatedTime = Time.time,
            Name = entry.Name,
        });
        Diag?.Info(
            $"LoaderHookPrediction: predicted '{entry.Name}' visual spawned predictionId={controller.predictionId}; " +
            "hookInstance/rope untouched, waiting for handoff/exit/timeout.");
    }

    private static void DropExpiredPredictedHookPostfix(ProjectileGrappleController __instance)
    {
        var self = instance;
        if (self == null || __instance == null || self.pending.Count == 0)
        {
            return;
        }

        var controller = __instance.GetComponent<ProjectileController>();
        if (controller == null || !controller.isPrediction)
        {
            return;
        }

        self.Guard(() => self.DropPending(controller, handedOff: false, fireHookExited: false));
    }

    private static void DropPredictedHookOnFireHookExitPostfix(FireHook __instance)
    {
        var self = instance;
        if (self == null || __instance == null || self.pending.Count == 0)
        {
            return;
        }

        self.Guard(() => self.DropPendingOwnedBy(__instance.outer != null ? __instance.outer.gameObject : null));
    }

    private void DropPendingOnHandoff(ProjectileController authorityProjectile)
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].PredictionId != authorityProjectile.predictionId)
            {
                continue;
            }

            DropAt(i, handedOff: true, fireHookExited: false);
            return;
        }
    }

    private void DropPending(ProjectileController controller, bool handedOff, bool fireHookExited)
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(pending[i].Controller, controller))
            {
                continue;
            }

            DropAt(i, handedOff, fireHookExited);
            return;
        }
    }

    private void DropPendingOwnedBy(GameObject owner)
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(pending[i].Owner, owner))
            {
                continue;
            }

            DropAt(i, handedOff: false, fireHookExited: true);
        }
    }

    /// <summary>
    /// Removes one tracked predicted copy. Destroy is deferred and Unity-null checked: an instance
    /// already gone (vanilla ReleasePredictionId or another path) is only forgotten, never destroyed
    /// twice; a handed-off ghost is no longer referenced by this object (handoff set it null first).
    /// </summary>
    private void DropAt(int index, bool handedOff, bool fireHookExited)
    {
        var entry = pending[index];
        bool alreadyDestroyed = entry.Controller == null || entry.Controller.gameObject == null;
        var reason = PredictionMath.PredictedHookDrop(
            handedOff,
            fireHookExited,
            alreadyDestroyed,
            Time.time - entry.CreatedTime,
            CurrentRoundTripSeconds(),
            FeedbackCapSeconds());
        if (alreadyDestroyed)
        {
            pending.RemoveAt(index);
            return;
        }

        if (reason == PredictedHookDropReason.None)
        {
            return;
        }

        pending.RemoveAt(index);
        bool ghostAlreadyTransferred = entry.Controller.ghost == null;
        UnityEngine.Object.Destroy(entry.Controller.gameObject);
        Diag?.Info(
            $"LoaderHookPrediction: dropped predicted '{entry.Name}' visual ({reason}) after " +
            $"{Time.time - entry.CreatedTime:F2}s predictionId={entry.PredictionId} " +
            $"ghostAlreadyTransferred={ghostAlreadyTransferred}.");
    }

    private double CurrentRoundTripSeconds()
        => Latency != null && Latency.TryGetTimeoutRoundTrip(out var roundTrip) ? roundTrip : 0.0;

    private double FeedbackCapSeconds()
        => feedbackFallbackMaxSeconds != null
            ? feedbackFallbackMaxSeconds()
            : PredictionMath.MaxStuckTimeoutSeconds;
}
