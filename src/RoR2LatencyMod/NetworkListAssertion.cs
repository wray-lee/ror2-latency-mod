using System;
using System.Linq;
using BepInEx.Logging;
using RoR2;

namespace RoR2LatencyMod;

internal static class NetworkListAssertion
{
    internal static void Run(ManualLogSource log)
    {
        var modList = NetworkModCompatibilityHelper.networkModList;
        var modHash = NetworkModCompatibilityHelper.networkModHash;

        log.LogInfo($"Network mod list: {(modList == null ? "<null>" : "[" + string.Join(", ", modList) + "]")}");
        log.LogInfo($"Network mod hash: {modHash ?? "<null>"}");

        if (modList == null)
        {
            log.LogError($"ASSERTION FAILED: {Plugin.PluginGuid} could not be checked because the network mod list is null.");
            return;
        }

        var entry = modList.FirstOrDefault(item =>
            string.Equals(item.Split(';')[0], Plugin.PluginGuid, StringComparison.OrdinalIgnoreCase));

        if (entry != null)
        {
            log.LogError($"ASSERTION FAILED: {Plugin.PluginGuid} is in the network mod list as \"{entry}\". " +
                         "A client-only mod must not require sync; check [NetworkCompatibility(CompatibilityLevel.NoNeedForSync)] " +
                         "and the hard R2API dependency.");
            return;
        }

        log.LogInfo($"OK: {Plugin.PluginGuid} is not in the network mod list.");
    }
}
