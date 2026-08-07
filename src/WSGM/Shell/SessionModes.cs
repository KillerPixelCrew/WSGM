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

    /// <summary>Raised (on the caller's thread) when <see cref="StartOrFocusSteam"/>
    /// could not bring Steam up, with the user-facing warning text.</summary>
    public event Action<string>? SteamStartFailed;

    /// <summary>Raised (on the caller's thread) during a desktop-mode transition,
    /// after Steam left Big Picture but BEFORE explorer starts. Listeners that own
    /// per-game-mode resources which must not coexist with explorer (the tray host's
    /// Shell_TrayWnd — explorer's taskbar creates its own) tear down here.</summary>
    public event Action? DesktopModeStarting;

    /// <summary>Raised (on the caller's thread) after a game-mode transition has
    /// removed explorer from the session. Listeners recreate per-game-mode
    /// resources (tray host) here.</summary>
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

    /// <summary>Desktop mode: stop reacting to Steam (no auto-relaunch, no overlay
    /// pop), drop Steam out of Big Picture, bring the desktop up.</summary>
    public void EnterDesktopMode()
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
    /// back (the protocol also boots Steam if it exited while on the desktop).</summary>
    public void EnterGameMode()
    {
        Log.Info("Entering game mode.");
        ExplorerControl.KillExplorer();
        ApplyGameModePosture();
        GameModeEntered?.Invoke();
        if (_monitor is not null)
        {
            _monitor.Paused = false;
        }
        StartOrFocusSteam();
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
