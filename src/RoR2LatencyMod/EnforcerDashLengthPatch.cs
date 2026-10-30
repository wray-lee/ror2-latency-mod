// EnforcerDashLength：按倍率拉长 Enforcer 冲刺（玩法加强，默认 1 = 原版）。
//
// 反编译核对（F:/ror2-decomp/RoR2src，只读）：
// - BaseShieldDashPrepCharge.cs:239 ModifyNextState 把 prepPassingParamaters 的覆盖时长写进
//   下一个 IShieldDashSpeedAndDuration（ProxyShieldDash，:267-277）；ProxyShieldDash.cs:110-117
//   OnSerialize 把 inOverrideDurationFromAuthority 原样发给主机，InitializeStateServer(:186-216)
//   据此设定载具 inDurationAuthority，无范围校验。
// - VehicleShieldDashHeavy.cs:224 fixedAge > inDurationAuthority 才退出，:301 速度曲线按
//   fixedAge / inDurationAuthority 归一化，所以时长 ×m 即距离 ×m、速度不变。
// - 本补丁在发送前（ModifyNextState 之后、OnEnter/序列化之前）把本机权威的覆盖时长乘倍率；
//   GhostDash/LocalEnd 读的是同一字段，预测会自动跟上。伤害/命中仍由主机结算。
using System;
using EntityStates;
using EntityStates.Enforcer;
using HarmonyLib;
using RoR2;

namespace RoR2LatencyMod;

public sealed class EnforcerDashLengthPatch : SkillPatch
{
    private const string Tag = "EnforcerDashLength";

    private static EnforcerDashLengthPatch instance;

    public EnforcerDashLengthPatch(Func<float> lengthMultiplier, DiagLog diag = null)
        : base("EnforcerDashLength", PatchSide.Client, () => true, diag)
    {
        LengthMultiplier = lengthMultiplier;
    }

    /// <summary>Dash duration (and so distance) multiplier, 1 = vanilla.</summary>
    public Func<float> LengthMultiplier { get; }

    // Not latency compensation: the host's own InitializeStateServer reads the same field.
    protected override bool RequiresRemoteClient => false;

    protected override void Install()
    {
        var modifyNextState = AccessTools.Method(
            typeof(BaseShieldDashPrepCharge), nameof(BaseShieldDashPrepCharge.ModifyNextState),
            new[] { typeof(EntityState) });
        if (modifyNextState == null)
        {
            throw new MissingMemberException("BaseShieldDashPrepCharge.ModifyNextState not found");
        }

        instance = this;
        new Harmony(Plugin.PluginGuid + "." + Name).Patch(
            modifyNextState,
            postfix: new HarmonyMethod(typeof(EnforcerDashLengthPatch), nameof(ModifyNextStatePostfix)));
        Diag?.Info($"{Tag}: installed (owner-side dash duration multiplier).");
    }

    private static void ModifyNextStatePostfix(EntityState __instance, EntityState nextState)
    {
        var self = instance;
        if (self == null || !self.ShouldRun() || !(nextState is IShieldDashSpeedAndDuration dash))
        {
            return;
        }

        self.Guard(() => self.Scale(__instance, dash));
    }

    private void Scale(EntityState prep, IShieldDashSpeedAndDuration dash)
    {
        double multiplier = PredictionMath.BuffMultiplier(
            LengthMultiplier != null ? LengthMultiplier() : 1f);
        var identity = prep.outer != null ? prep.outer.networkIdentity : null;
        if (multiplier <= 1.0 || identity == null || !Util.HasEffectiveAuthority(identity))
        {
            return;
        }

        float duration = dash.inOverrideDurationFromAuthority;
        dash.inOverrideDurationFromAuthority = (float)(duration * multiplier);
        Diag?.Info(
            $"{Tag}: dash duration x{multiplier:F2} ({duration:F2}s -> {dash.inOverrideDurationFromAuthority:F2}s).");
    }
}
