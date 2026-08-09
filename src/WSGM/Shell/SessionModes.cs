using System;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Session-mode coordinator: owns the game/desktop mode transitions
/// (explorer, display scale, Steam open/close, monitor pause) and
/// the shared Steam start + warning flow. ShellSession uses it at boot, the
/// overlay's buttons drive it at runtime; OverlayController stays the UI owner
/// (lease lifecycle, window) and surfaces warnings via <see cref="SteamStartFailed"/>.</summary>
public sealed class SessionModes
{
    /// <summary>The warning shown when the required Steam installation cannot be found.</summary>
    public const string SteamNotFoundWarning = "Steam was not found on this PC. Install Steam — WSGM is Steam-exclusive.";

    /// <summary>The warning shown when Steam Big Picture could not be started.</summary>
    public const string BigPictureStartFailedWarning = "Couldn't start Steam Big Picture.";

    private AppConfig _config;
    private readonly SteamMonitor? _monitor;
    private readonly object _homeLaunchGate = new();
    private bool _homeLaunchInProgress;
    private DateTime _lastHomeLaunchUtc;

    private static readonly TimeSpan HomeLaunchCooldown = TimeSpan.FromSeconds(5);
    // Budget for the WHOLE orderly exit: the first attempt including
    // ExplorerControl's 8 s linger grace (device-proven 2026-08-09: remnants
    // can outlive the old 2 s grace, and terminating them is what got the shell
    // respawned), plus the respawn retry, which shares this same deadline
    // rather than starting a fresh one. The transition still fails open when
    // explorer is genuinely wedged.
    private static readonly TimeSpan ExplorerExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The warning shown when explorer refused its orderly exit and the
    /// session stayed in desktop mode (fail open, never a half game mode).</summary>
    public const string ExplorerExitFailedWarning =
        "Couldn't exit Windows Explorer safely. Desktop mode was preserved.";

    /// <summary>Raised (on the caller's thread) when <see cref="StartOrFocusSteam"/>
    /// could not bring Steam up, with the user-facing warning text.</summary>
    public event Action<string>? SteamStartFailed;

    /// <summary>Raised (on the caller's thread) during a desktop-mode transition,
    /// after Steam left Big Picture but BEFORE explorer starts. Listeners that own
    /// per-game-mode resources which must not coexist with explorer (the tray host's
    /// Shell_TrayWnd — explorer's taskbar creates its own) tear down here.</summary>
    public event Action? DesktopModeStarting;

    /// <summary>Raised (on the UI thread — the transition completes there after the
    /// off-thread explorer shutdown) after a game-mode transition has removed
    /// explorer from the session. Listeners recreate per-game-mode resources
    /// (tray host) here.</summary>
    public event Action? GameModeEntered;

    /// <summary>Creates the coordinator for desktop/game transitions.</summary>
    /// <param name="config">The initial configuration controlling display posture and launch behavior.</param>
    /// <param name="monitor">The optional Steam monitor to pause or resume during transitions.</param>
    public SessionModes(AppConfig config, SteamMonitor? monitor)
    {
        _config = config;
        _monitor = monitor;
    }

    /// <summary>Applies a freshly loaded config (settings saved in another process).
    /// Reloads replace the config wholesale, so no runtime state may live on it.</summary>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
    }

    /// <summary>Applies game mode's 100% display scaling. Windows exclusively
    /// owns device posture and touch-keyboard policy.</summary>
    public void ApplyGameModePosture()
    {
        DisplayScale.ApplyGameMode(_config);
    }

    private int _explorerTransition;

    /// <summary>True while explorer is being brought up or down (mode switch or the
    /// boot takeover). Mode-switch requests arriving in that window are ignored —
    /// two concurrent explorer transitions produced exactly the device-observed
    /// mess of duplicate shutdowns and refused tray hosts (2026-08-07).</summary>
    public bool TransitionInProgress => System.Threading.Volatile.Read(ref _explorerTransition) != 0;

    /// <summary>Marks an explorer transition as running (boot takeover uses this
    /// directly; the mode switches go through <see cref="TryBeginTransition"/>).</summary>
    internal void BeginTransition() => System.Threading.Volatile.Write(ref _explorerTransition, 1);

    /// <summary>Clears the transition flag. Always pair with Begin/TryBegin.</summary>
    internal void EndTransition() => System.Threading.Volatile.Write(ref _explorerTransition, 0);

    private bool TryBeginTransition(string reason)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _explorerTransition, 1, 0) != 0)
        {
            Log.Warn($"Ignoring {reason}: an explorer transition is already in progress.");
            return false;
        }
        return true;
    }

    /// <summary>Desktop mode: stop reacting to Steam (no auto-relaunch, no overlay
    /// pop), drop Steam out of Big Picture, bring the desktop up.</summary>
    public void EnterDesktopMode()
    {
        if (!TryBeginTransition("desktop-mode switch"))
        {
            return;
        }
        try
        {
            Log.Info("Entering desktop mode.");
            if (_monitor is not null)
            {
                _monitor.Paused = true;
            }
            ExitBigPicture();
            DisplayScale.RestoreSaved(_config);
            DesktopModeStarting?.Invoke();
            ExplorerControl.StartExplorer();
        }
        finally
        {
            EndTransition();
        }
    }

    /// <summary>Plain desktop Steam start — no Big Picture. Used by the boot
    /// splash's Switch-to-desktop: the boot sequence skips its Big Picture start
    /// once the monitor is paused, but the session should still end up with Steam
    /// available in windowed mode. No-op when Steam already runs.</summary>
    public void StartSteamDesktop()
    {
        if (Steam.IsRunning)
        {
            return;
        }
        if (Steam.ExePath is { } exe)
        {
            Log.Info("Starting Steam (desktop mode, no Big Picture).");
            AppLauncher.Start(exe, "", elevated: false);
        }
    }

    /// <summary>Game mode: desktop goes away, monitoring resumes, Big Picture comes
    /// back (the protocol also boots Steam if it exited while on the desktop).
    /// Returns immediately — the explorer shutdown can take seconds and runs off
    /// the UI thread (a synchronous shutdown froze the overlay for its full
    /// duration, device-observed 2026-08-07); the rest of the transition posts
    /// back to the UI thread once explorer is gone.</summary>
    public void EnterGameMode()
    {
        if (!TryBeginTransition("game-mode switch"))
        {
            return;
        }
        Log.Info("Entering game mode.");
        System.Threading.Tasks.Task.Run(() =>
        {
            var exited = false;
            try
            {
                exited = ExplorerControl.ExitExplorerAndWait(ExplorerExitTimeout);
            }
            catch (Exception ex)
            {
                Log.Error("Explorer exit failed", ex);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (!exited && ExplorerControl.IsRunningInSession())
                    {
                        // Fail open (era-proven UX): a half-removed desktop with a
                        // refused tray host is strictly worse than staying put.
                        Log.Warn("Could not exit explorer safely — staying in desktop mode.");
                        SteamStartFailed?.Invoke(ExplorerExitFailedWarning);
                        return;
                    }
                    ApplyGameModePosture();
                    GameModeEntered?.Invoke();
                    if (_monitor is not null)
                    {
                        _monitor.Paused = false;
                    }
                    StartOrFocusSteam();
                }
                finally
                {
                    EndTransition();
                }
            });
        });
    }

    /// <summary>Asks Steam to leave Big Picture (Steam keeps running). No-op if
    /// Steam isn't running.</summary>
    public void ExitBigPicture()
    {
        // Live check, not the up-to-5 s-stale monitor poll: entering desktop mode
        // right after Steam started must still send the close URL.
        if (!Steam.IsRunning)
        {
            return;
        }
        Log.Info("Exiting Steam Big Picture.");
        AppLauncher.StartProtocol(Steam.CloseBigPictureUrl);
    }

    /// <summary>Deliberately stops Steam (graceful steam://exit). Pauses the monitor
    /// first so neither auto-relaunch nor the exit-overlay reaction fires.</summary>
    public void CloseSteam()
    {
        if (_monitor is not null)
        {
            _monitor.Paused = true;
        }
        Log.Info("Closing Steam (steam://exit).");
        AppLauncher.StartProtocol(Steam.ExitUrl);
    }

    /// <summary>Start and focus are the same operation: steam://open/bigpicture
    /// re-activates a running Big Picture (UIPI-proof) and boots Steam when it
    /// isn't running. Re-arms the monitor (desktop mode and close-Steam pause it).
    /// Failures surface through <see cref="SteamStartFailed"/>.</summary>
    public void StartOrFocusSteam()
    {
        if (_monitor is not null)
        {
            _monitor.Paused = false;
        }
        if (_monitor?.IsAlive == true)
        {
            FocusSteam();
            return;
        }

        if (!TryBeginHomeLaunch())
        {
            return;
        }

        try
        {
            var warning = StartBigPicture();
            if (warning is not null)
            {
                SteamStartFailed?.Invoke(warning);
            }
        }
        finally
        {
            EndHomeLaunch();
        }
    }

    /// <summary>The one Steam start + warning flow (shared by boot and the overlay):
    /// install check, then Big Picture launch. Returns the user-facing warning to
    /// surface, or null on success.</summary>
    public string? StartBigPicture()
    {
        if (!Steam.IsInstalled)
        {
            Log.Warn("Steam is not installed — showing overlay instead.");
            return SteamNotFoundWarning;
        }
        Log.Info("Starting Steam Big Picture.");
        var result = Steam.LaunchBigPicture();
        return result.Started ? null : BigPictureStartFailedWarning;
    }

    /// <summary>Brings Steam Big Picture to the foreground when the monitor sees it alive.</summary>
    public void FocusSteam()
    {
        if (_monitor?.IsAlive == true)
        {
            // Protocol re-activation self-focuses even against an elevated target.
            AppLauncher.StartProtocol(Steam.OpenBigPictureUrl);
        }
    }

    private bool TryBeginHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            if (_homeLaunchInProgress || DateTime.UtcNow - _lastHomeLaunchUtc < HomeLaunchCooldown)
            {
                Log.Warn("Skipping duplicate home-app start request.");
                return false;
            }
            _homeLaunchInProgress = true;
            return true;
        }
    }

    private void EndHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            _homeLaunchInProgress = false;
            _lastHomeLaunchUtc = DateTime.UtcNow;
        }
    }

}
