namespace WSGM.Core;

/// <summary>Process-wide owner of the Steam Input layout pin (steam://forceinputappid).
/// Pinning a stock gamepad layout (default 480/Spacewar — every account owns it)
/// keeps the controller readable system-wide no matter which window has focus.
/// Without the pin, Steam's desktop profile swallows the pad from every input API
/// the moment a non-game window (our overlay) is focused. Confirmed on device.
///
/// The pin lives inside Steam and SURVIVES our process dying, so every exit and
/// recovery path must call <see cref="ReleaseBestEffort"/> — including recovery
/// runs in fresh processes that cannot know whether a crashed shell pinned.</summary>
public static class SteamInputPin
{
    private static int _applied;

    /// <summary>True when this process pinned a layout and hasn't released it.</summary>
    public static bool IsApplied => _applied > 0;

    /// <summary>Idempotent. No-ops unless Steam is running — the protocol URL would
    /// otherwise boot Steam just to configure it.</summary>
    public static void Apply(int appId)
    {
        if (appId < 0 || appId == _applied)
        {
            return;
        }
        if (!SteamRunning())
        {
            // A release (0) while Steam is gone still reached the desired state —
            // Steam restarts unpinned. Forget the stale pin, or appId == _applied
            // would block every re-pin for the rest of the session.
            if (appId == 0)
            {
                _applied = 0;
                Log.Info("Steam Input pin state cleared (Steam not running).");
            }
            return;
        }
        if (!AppLauncher.StartProtocol($"steam://forceinputappid/{appId}").Started)
        {
            return; // leave _applied untouched so the next Apply retries
        }
        Log.Info(appId > 0 ? $"Steam Input pinned to appid {appId}." : "Steam Input pin released.");
        _applied = appId;
    }

    /// <summary>Fires the /0 reset whenever Steam is running, regardless of what this
    /// process believes was applied. Never throws — it runs on panic/recovery paths.</summary>
    public static void ReleaseBestEffort(string reason)
    {
        try
        {
            if (SteamRunning())
            {
                AppLauncher.StartProtocol("steam://forceinputappid/0");
                Log.Info($"Steam Input pin released ({reason}).");
            }
            _applied = 0;
        }
        catch
        {
            // Recovery paths must never be blocked by pin cleanup.
        }
    }

    private static bool SteamRunning() => WindowFinder.FindProcessIds(Steam.MainProcessName).Count > 0;
}
