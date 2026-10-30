using System;
using RoR2.Networking;
using UnityEngine;

namespace RoR2LatencyMod;

/// <summary>
/// Runtime binding of <see cref="LatencyEstimator"/> to the game's client-side
/// frame-smoothed RTT. Kept out of the framework types so tests never load game assemblies.
/// </summary>
public static class GameLatency
{
    public static LatencyEstimator Create()
        => new LatencyEstimator(
            ReadSmoothedRttSeconds,
            () => Time.time,
            IsHost);

    /// <summary>Enforcer variant: measured enter-&gt;arrival samples, capped initial game RTT.</summary>
    public static LatencyEstimator CreateEnforcer(
        MeasuredRttEstimator measured,
        Func<double> initialRttCapSeconds)
        => new LatencyEstimator(
            ReadSmoothedRttSeconds,
            () => Time.time,
            IsHost,
            measured,
            initialRttCapSeconds);

    /// <summary>Client frame-smoothed RTT in seconds; 0 before a client connection exists.</summary>
    public static double ReadSmoothedRttSeconds()
    {
        NetworkManagerSystem manager = NetworkManagerSystem.singleton;
        return manager != null ? manager.filteredClientRttFrame : 0.0;
    }

    /// <summary>True on the host process; every client patch must fall through to vanilla there.</summary>
    public static bool IsNetworkServerActive() => UnityEngine.Networking.NetworkServer.active;

    private static bool IsHost()
    {
        NetworkManagerSystem manager = NetworkManagerSystem.singleton;
        return manager != null && manager.IsHost();
    }
}
