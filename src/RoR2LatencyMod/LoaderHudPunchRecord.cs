// Loader HUD 4：拳速记录，最近 N 次蓄力拳命中显示“角度 : 速度”（配置在
// PatchConfig.HudPunchRecord，默认关；条数见 HUD_PunchRecord_Count，位置用通用锚点/偏移）。
//
// 事件挂点（只读反编译 F:/ror2-decomp/RoR2src/EntityStates.Loader 核对，独立实现）：
// - BaseSwingChargedFist.cs:47-59 OnEnter：authority 下写 punchVelocity/punchSpeed，
//   所以这里是“一次拳击”的开始点（记录速度与瞄准点积）。
// - BaseSwingChargedFist.cs:101-105 OnMeleeHitAuthority 触发 onHitAuthorityGlobal；
//   EntityStates/BasicMeleeAttack.cs:290-301 每个命中的 FixedUpdate 只回调一次（多目标也只一次）。
// - GroundSlam.cs:62-76 FixedUpdate 在落地时 :72 调 DetonateAuthority；:98-130 爆炸并返回
//   BlastAttack.Result（RoR2/BlastAttack.cs:52-57 的 hitCount>0 才算雷霆重击命中）。
// 灰化窗口：同一次拳击从 OnEnter 到第一次命中之间，若 GroundSlam 命中过，则该条 SlammedFirst。
// 去重：tracker 在第一次命中后移除 pending，同一拳击的后续多目标/多段命中不再入账；
// BuildPunchRecords 再按 PunchId 兜底去重。
// 高频路径：事件回调只在命中/重击时执行，Read 只在版本变化时重建字符串，无逐帧 LINQ/反射。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using EntityStates.Loader;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace RoR2LatencyMod;

/// <summary>拳速记录的纯展示规则；无 Unity 类型，可单测。</summary>
public static class LoaderHudPunchRecordMath
{
    public const int DefaultRecordCount = 3;
    public const int MinRecordCount = 1;
    public const int MaxRecordCount = 10;

    /// <summary>雷霆重击先命中时固定用灰色小字（相对配置字号）。</summary>
    public const double SlammedFontScale = 0.7;

    public static int ClampRecordCount(int requested)
    {
        return requested < MinRecordCount ? MinRecordCount
            : requested > MaxRecordCount ? MaxRecordCount
            : requested;
    }

    public static double RecordFontScale(double speedMetersPerSecond, bool slammedFirst)
    {
        return slammedFirst
            ? SlammedFontScale
            : LoaderHudMath.SpeedFontSize(speedMetersPerSecond, 1.0, 1.6);
    }

    /// <summary>单行“角度 : 速度”；角度是速度·瞄准点积的 0-100 百分比。</summary>
    public static string FormatRecordLine(PunchRecord record)
    {
        string angle = ((int)Math.Round(
            LoaderHudMath.AlignmentPercent(record.AimDot), MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture);
        string speed = ((int)Math.Round(record.Speed, MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture);
        string body = angle + " : " + speed;

        if (record.SlammedFirst)
        {
            return "<color=#808080><size=70%>" + body + "</size></color>";
        }

        int scalePercent = (int)Math.Round(
            RecordFontScale(record.Speed, false) * 100.0, MidpointRounding.AwayFromZero);
        return "<size=" + scalePercent.ToString(CultureInfo.InvariantCulture) + "%>" + body + "</size>";
    }
}

/// <summary>
/// 纯记录状态机：一次拳击只收第一条命中；雷霆重击只在“拳击开始后、首次命中前”标记灰化。
/// 钩子代码只负责把游戏事件转成这些整数/时间参数。
/// </summary>
public sealed class LoaderHudPunchTracker
{
    public const int MaxStoredHits = 64;

    private struct PendingPunch
    {
        public int BodyId;
        public int PunchId;
        public double Speed;
        public double AimDot;
        public double SlamSeconds;
    }

    private struct StoredHit
    {
        public int BodyId;
        public PunchHit Hit;
    }

    private readonly List<PendingPunch> pending = new List<PendingPunch>(2);
    private readonly List<StoredHit> hits = new List<StoredHit>(16);
    private readonly List<PunchHit> bodyHits = new List<PunchHit>(16);
    private int nextPunchId;

    /// <summary>命中入账或清空时递增；HUD 用它避免逐帧重建。</summary>
    public int Version { get; private set; }

    public void BeginPunch(int bodyId, double speed, double aimDot)
    {
        var entry = new PendingPunch
        {
            BodyId = bodyId,
            PunchId = ++nextPunchId,
            Speed = NonNegativeFinite(speed),
            AimDot = ClampAimDot(aimDot),
            SlamSeconds = -1.0,
        };

        int index = IndexOfPending(bodyId);
        if (index >= 0)
        {
            pending[index] = entry;
        }
        else
        {
            pending.Add(entry);
        }
    }

    /// <summary>GroundSlam 爆炸且确实命中目标时调用；只在拳击窗口内有效。</summary>
    public void ObserveSlam(int bodyId, double slamSeconds, bool hitAnything)
    {
        if (!hitAnything || double.IsNaN(slamSeconds) || double.IsInfinity(slamSeconds) || slamSeconds < 0.0)
        {
            return;
        }

        int index = IndexOfPending(bodyId);
        if (index < 0)
        {
            return;
        }

        PendingPunch entry = pending[index];
        entry.SlamSeconds = slamSeconds;
        pending[index] = entry;
    }

    /// <summary>首次命中返回 true；同一拳击后续目标返回 false（多目标一拳一条）。</summary>
    public bool TryRecordHit(int bodyId, double hitSeconds)
    {
        if (double.IsNaN(hitSeconds) || double.IsInfinity(hitSeconds) || hitSeconds < 0.0)
        {
            return false;
        }

        int index = IndexOfPending(bodyId);
        if (index < 0)
        {
            return false;
        }

        PendingPunch entry = pending[index];
        pending.RemoveAt(index);
        hits.Add(new StoredHit
        {
            BodyId = bodyId,
            Hit = new PunchHit(entry.PunchId, hitSeconds, entry.SlamSeconds, entry.Speed, entry.AimDot),
        });

        if (hits.Count > MaxStoredHits)
        {
            hits.RemoveAt(0);
        }

        Version++;
        return true;
    }

    /// <summary>该 body 最近 N 条，最新在前、N 截断；排序由 BuildPunchRecords 保证稳定。</summary>
    public PunchRecord[] Snapshot(int bodyId, int maxCount)
    {
        bodyHits.Clear();
        for (int i = 0; i < hits.Count; i++)
        {
            if (hits[i].BodyId == bodyId)
            {
                bodyHits.Add(hits[i].Hit);
            }
        }

        return LoaderHudMath.BuildPunchRecords(
            bodyHits, LoaderHudPunchRecordMath.ClampRecordCount(maxCount));
    }

    public void Clear()
    {
        pending.Clear();
        hits.Clear();
        bodyHits.Clear();
        Version++;
    }

    private int IndexOfPending(int bodyId)
    {
        for (int i = 0; i < pending.Count; i++)
        {
            if (pending[i].BodyId == bodyId)
            {
                return i;
            }
        }

        return -1;
    }

    private static double ClampAimDot(double dot)
    {
        if (double.IsNaN(dot))
        {
            return 0.0;
        }

        return dot > 1.0 ? 1.0 : dot < -1.0 ? -1.0 : dot;
    }

    private static double NonNegativeFinite(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 ? 0.0 : value;
    }
}

public sealed class LoaderHudPunchRecord : LoaderHudElement
{
    /// <summary>可选覆盖：返回 null 时用内置事件记录。</summary>
    public static Func<LoaderHudContext, IReadOnlyList<PunchHit>> PunchHitsProvider;

    private static string Label => LabelTable.Get("hud.label.punchSpeed", LabelLanguageProbe.Current);
    private const string ConfigSection = "Loader HUD";
    private const string CountKey = "HUD_PunchRecord_Count";

    private static readonly LoaderHudPunchTracker Tracker = new LoaderHudPunchTracker();
    private static readonly ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("RoR2LatencyMod.LoaderHud");

    private static LoaderHudConfig recordingConfig;
    private static ConfigEntry<int> recordCount;
    private static Harmony harmony;
    private static bool hooksInstalled;
    private static bool hooksFailed;
    private static bool hookFaulted;
    private static int liveInstances;
    private static LoaderHudPunchRecord active;

    private readonly StringBuilder valueBuilder = new StringBuilder(128);
    private int cachedVersion = -1;
    private int cachedBodyId = int.MinValue;
    private int cachedCount = -1;
    private IReadOnlyList<PunchHit> cachedExternal;
    private int cachedExternalCount = -1;
    private string cachedValue;

    private void Awake()
    {
        active = this;
        liveInstances++;
        EnsureHooks();
    }

    private void OnDestroy()
    {
        if (active == this)
        {
            active = null;
        }

        // 最后一个拳速记录 HUD 销毁（宿主已全关/换局）：卸载钩子并清空记录。
        if (--liveInstances > 0)
        {
            return;
        }

        liveInstances = 0;
        ShutdownHooks();
    }

    private static void ShutdownHooks()
    {
        try
        {
            BaseSwingChargedFist.onHitAuthorityGlobal -= OnChargedFistHit;
            harmony?.UnpatchSelf();
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "[LoaderHud] 拳速记录钩子卸载失败（只影响残留开销）：" +
                exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            harmony = null;
            hooksInstalled = false;
            hookFaulted = false;
            Tracker.Clear();
        }
    }

    protected override LoaderHudReadout Read(in LoaderHudContext context)
    {
        CharacterBody body = context.Body;
        if (body == null)
        {
            return LoaderHudReadout.None;
        }

        EnsureHooks();
        if (recordingConfig != Config)
        {
            recordingConfig = Config;
        }

        EnsureRecordCountEntry();

        int bodyId = body.GetInstanceID();
        int count = LoaderHudPunchRecordMath.ClampRecordCount(
            recordCount != null ? recordCount.Value : LoaderHudPunchRecordMath.DefaultRecordCount);

        IReadOnlyList<PunchHit> external = PunchHitsProvider != null ? PunchHitsProvider(context) : null;
        bool changed;
        PunchRecord[] records = null;
        if (external != null)
        {
            changed = !ReferenceEquals(external, cachedExternal) ||
                external.Count != cachedExternalCount ||
                bodyId != cachedBodyId ||
                count != cachedCount;
            cachedExternal = external;
            cachedExternalCount = external.Count;
            if (changed)
            {
                records = LoaderHudMath.BuildPunchRecords(external, count);
            }
        }
        else
        {
            cachedExternal = null;
            cachedExternalCount = -1;
            changed = Tracker.Version != cachedVersion || bodyId != cachedBodyId || count != cachedCount;
            if (changed)
            {
                records = Tracker.Snapshot(bodyId, count);
            }
        }

        if (changed)
        {
            cachedVersion = Tracker.Version;
            cachedBodyId = bodyId;
            cachedCount = count;
            cachedValue = BuildValue(records);
        }

        return string.IsNullOrEmpty(cachedValue)
            ? LoaderHudReadout.None
            : new LoaderHudReadout(Label, cachedValue);
    }

    private string BuildValue(PunchRecord[] records)
    {
        if (records == null || records.Length == 0)
        {
            return string.Empty;
        }

        valueBuilder.Clear();
        for (int i = 0; i < records.Length; i++)
        {
            if (i > 0)
            {
                valueBuilder.Append('\n');
            }

            valueBuilder.Append(LoaderHudPunchRecordMath.FormatRecordLine(records[i]));
        }

        return valueBuilder.ToString();
    }

    private static void EnsureRecordCountEntry()
    {
        if (recordCount != null || recordingConfig == null || recordingConfig.Enabled == null)
        {
            return;
        }

        try
        {
            ConfigFile file = recordingConfig.Enabled.ConfigFile;
            if (file == null)
            {
                return;
            }

            recordCount = file.Bind(
                ConfigSection,
                CountKey,
                LoaderHudPunchRecordMath.DefaultRecordCount,
                new ConfigDescription(
                    LabelTable.Get("hud.desc.punchCount", LabelLanguageProbe.Current),
                    new AcceptableValueRange<int>(
                        LoaderHudPunchRecordMath.MinRecordCount,
                        LoaderHudPunchRecordMath.MaxRecordCount)));
        }
        catch (Exception ex)
        {
            Log.LogWarning(
                "[LoaderHud] 拳速记录条数配置未绑定，使用默认 3：" +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void EnsureHooks()
    {
        if (hooksInstalled || hooksFailed)
        {
            return;
        }

        try
        {
            var onEnter = AccessTools.Method(
                typeof(BaseSwingChargedFist), nameof(BaseSwingChargedFist.OnEnter));
            var detonate = AccessTools.Method(typeof(GroundSlam), "DetonateAuthority");
            if (onEnter == null || detonate == null)
            {
                throw new MissingMethodException(
                    "BaseSwingChargedFist.OnEnter=" + (onEnter != null) +
                    ", GroundSlam.DetonateAuthority=" + (detonate != null));
            }

            harmony = new Harmony(Plugin.PluginGuid + ".LoaderHud.PunchRecord");
            harmony.Patch(onEnter, postfix: new HarmonyMethod(
                typeof(LoaderHudPunchRecord), nameof(SwingOnEnterPostfix)));
            harmony.Patch(detonate, postfix: new HarmonyMethod(
                typeof(LoaderHudPunchRecord), nameof(GroundSlamDetonatePostfix)));
            BaseSwingChargedFist.onHitAuthorityGlobal += OnChargedFistHit;
            hooksInstalled = true;
        }
        catch (Exception ex)
        {
            hooksFailed = true;
            Log.LogWarning(
                "[LoaderHud] 拳速记录钩子安装失败，该项只显示空（其它 HUD/补丁不受影响）：" +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool RecordingEnabled
    {
        get
        {
            return !hooksFailed && !hookFaulted &&
                recordingConfig != null &&
                recordingConfig.Enabled != null &&
                recordingConfig.Enabled.Value;
        }
    }

    // Harmony postfixes and the static hit event run inside gameplay code: never let an
    // exception escape, and latch this HUD off instead of retrying every hit.
    private static void FaultHook(Exception exception)
    {
        if (hookFaulted)
        {
            return;
        }

        hookFaulted = true;
        LoaderHudPunchRecord target = active;
        if (target != null)
        {
            target.Fault(exception, Log);
        }
        else
        {
            Log.LogWarning(
                "[LoaderHud] 拳速记录回调异常（HUD 未激活，已停用记录）：" +
                exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void SwingOnEnterPostfix(BaseSwingChargedFist __instance)
    {
        try
        {
            if (!RecordingEnabled || __instance == null)
            {
                return;
            }

            CharacterBody body = BodyOf(__instance);
            if (body == null || !body.hasAuthority)
            {
                return;
            }

            // CharacterMotor 的基类未引用：速度从 IPhysMotor 接口取；瞄准方向等价于 GetAimRay().direction。
            IPhysMotor motor = body.GetComponent<IPhysMotor>();
            Vector3 velocity = motor != null ? motor.velocity : Vector3.zero;
            double speed = __instance.punchSpeed;
            if (!(speed > 0.0))
            {
                speed = velocity.magnitude;
            }

            Vector3 aim = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
            double aimDot = LoaderHudMath.DirectionDot(
                new Vec3(velocity.x, velocity.y, velocity.z),
                new Vec3(aim.x, aim.y, aim.z));
            Tracker.BeginPunch(body.GetInstanceID(), speed, aimDot);
        }
        catch (Exception exception)
        {
            FaultHook(exception);
        }
    }

    private static void GroundSlamDetonatePostfix(GroundSlam __instance, BlastAttack.Result __result)
    {
        try
        {
            if (!RecordingEnabled || __instance == null || __result.hitCount <= 0)
            {
                return;
            }

            CharacterBody body = BodyOf(__instance);
            if (body == null || !body.hasAuthority)
            {
                return;
            }

            Tracker.ObserveSlam(body.GetInstanceID(), Time.timeAsDouble, true);
        }
        catch (Exception exception)
        {
            FaultHook(exception);
        }
    }

    private static void OnChargedFistHit(BaseSwingChargedFist instance)
    {
        try
        {
            if (!RecordingEnabled || instance == null)
            {
                return;
            }

            CharacterBody body = BodyOf(instance);
            if (body == null || !body.hasAuthority)
            {
                return;
            }

            Tracker.TryRecordHit(body.GetInstanceID(), Time.timeAsDouble);
        }
        catch (Exception exception)
        {
            FaultHook(exception);
        }
    }

    private static CharacterBody BodyOf(EntityStates.EntityState state)
    {
        EntityStateMachine machine = state != null ? state.outer : null;
        return machine != null ? machine.commonComponents.characterBody : null;
    }
}
