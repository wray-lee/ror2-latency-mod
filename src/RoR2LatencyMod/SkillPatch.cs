using System;

namespace RoR2LatencyMod;

/// <summary>Which side a patch belongs to. Host patches only install while hosting.</summary>
public enum PatchSide
{
    Client,
    Host,
}

/// <summary>
/// One self-contained skill fix. Each instance owns its own enable switch
/// (runtime: a BepInEx <c>ConfigEntry&lt;bool&gt;</c> exposed through this Func).
/// </summary>
public abstract class SkillPatch
{
    private readonly Func<bool> isEnabledByConfig;
    private Action<string> installLog;

    protected SkillPatch(
        string name,
        PatchSide side,
        Func<bool> isEnabledByConfig,
        DiagLog diag = null,
        LatencyEstimator latency = null)
    {
        Name = name;
        Side = side;
        this.isEnabledByConfig = isEnabledByConfig;
        Diag = diag;
        Latency = latency;
    }

    public string Name { get; }

    public PatchSide Side { get; }

    /// <summary>True once <see cref="Install"/> has completed successfully.</summary>
    public bool IsInstalled { get; private set; }

    /// <summary>True when this patch was installed with its switch on (install-time snapshot).</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>True once a hook body threw or the patch reported a fatal condition; never resets.</summary>
    public bool IsTripped { get; private set; }

    /// <summary>One-line state for the startup summary.</summary>
    public string InstallStatus { get; private set; } = "not-installed";

    /// <summary>
    /// Game-side host detector, wired once by the plugin (NetworkServer.active).
    /// Static so the framework keeps no game assemblies in its signatures; tests leave it false.
    /// </summary>
    public static Func<bool> IsHostDetector { get; set; } = () => false;

    /// <summary>
    /// True for patches that compensate for network latency and must fall through on the host.
    /// Pure local UI patches override this to false so they also run in single-player/host games.
    /// </summary>
    protected virtual bool RequiresRemoteClient => true;

    protected DiagLog Diag { get; }

    protected LatencyEstimator Latency { get; }

    /// <summary>
    /// Gate every hook body with this: installed, switch currently on (read live, so config
    /// edits apply immediately), not tripped, and (when required) this process is a remote client.
    /// </summary>
    public bool ShouldRun()
        => IsInstalled && isEnabledByConfig() && !IsTripped &&
           (!RequiresRemoteClient || !IsHostDetector());

    /// <summary>
    /// Runs a hook body. Any exception trips the patch; later calls return false without running
    /// the body, so the hook falls through to vanilla behaviour.
    /// </summary>
    public bool Guard(Action body)
    {
        if (body == null)
        {
            throw new ArgumentNullException(nameof(body));
        }

        if (IsTripped)
        {
            return false;
        }

        try
        {
            body();
            return true;
        }
        catch (Exception exception)
        {
            Trip($"hook exception: {exception}", installLog);
            return false;
        }
    }

    /// <summary>Latches this patch off for the rest of the session and logs why.</summary>
    protected void Trip(string reason, Action<string> log = null)
    {
        if (IsTripped)
        {
            return;
        }

        IsTripped = true;
        (log ?? installLog)?.Invoke($"Skill Patch '{Name}' tripped; falling back to vanilla: {reason}");
    }

    /// <summary>Installs this patch; a switch-off or any exception only disables this patch.</summary>
    public bool TryInstall(Action<string> log)
    {
        installLog = log;
        try
        {
            if (!isEnabledByConfig())
            {
                InstallStatus = "disabled-by-config";
                log?.Invoke($"Skill Patch '{Name}' disabled by config; not installed.");
                return false;
            }

            Install();
            IsInstalled = true;
            IsEnabled = true;
            InstallStatus = "installed+enabled";
            return true;
        }
        catch (Exception exception)
        {
            IsInstalled = false;
            IsEnabled = false;
            InstallStatus = "failed-to-install";
            log?.Invoke($"Skill Patch '{Name}' failed to install; disabled: {exception}");
            return false;
        }
    }

    /// <summary>Applies the patch. Throwing marks this patch failed.</summary>
    protected abstract void Install();
}

/// <summary>Runs patches in isolation so one failure cannot take down the others.</summary>
public sealed class SkillPatchInstaller
{
    private readonly SkillPatch[] patches;
    private readonly Func<bool> isHost;
    private readonly Action<string> log;

    public SkillPatchInstaller(Action<string> log, Func<bool> isHost, params SkillPatch[] patches)
    {
        this.log = log;
        this.isHost = isHost;
        this.patches = patches;
    }

    public void InstallAll()
    {
        foreach (var patch in patches)
        {
            if (patch.Side == PatchSide.Host && !isHost())
            {
                log?.Invoke($"Host Patch '{patch.Name}' skipped: not the host.");
                continue;
            }

            patch.TryInstall(log);
        }
    }
}
