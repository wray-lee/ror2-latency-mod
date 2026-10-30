using System;
using System.Collections.Generic;

namespace RoR2LatencyMod;

/// <summary>
/// Diagnostics sink for patches: plain Info/Warning plus per-key throttling for hot paths.
/// No BepInEx or game types, so tests can drive the clock and capture output.
/// </summary>
public sealed class DiagLog
{
    private readonly Action<string> info;
    private readonly Action<string> warning;
    private readonly Func<double> nowSeconds;
    private readonly double throttleSeconds;
    private readonly Dictionary<string, double> lastEmitByKey = new Dictionary<string, double>(StringComparer.Ordinal);

    public DiagLog(
        Action<string> info,
        Action<string> warning,
        Func<double> nowSeconds,
        double throttleSeconds = 1.0)
    {
        this.info = info;
        this.warning = warning;
        this.nowSeconds = nowSeconds;
        this.throttleSeconds = Math.Max(0.0, throttleSeconds);
    }

    public void Info(string message) => info?.Invoke(message);

    public void Warning(string message) => warning?.Invoke(message);

    /// <summary>Info line at most once per throttle window for the same key.</summary>
    public void Throttled(string key, string message)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        var now = nowSeconds();
        if (lastEmitByKey.TryGetValue(key, out var last) && now < last + throttleSeconds)
        {
            return;
        }

        lastEmitByKey[key] = now;
        info?.Invoke(message);
    }
}
