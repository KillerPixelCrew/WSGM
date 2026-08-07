using System;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Shell-mode orchestrator: starts startup apps and the home app, arms the
/// overlay (hotkey + edge swipes + home-exit), stays resident for the session.</summary>
public sealed class ShellSession
{
    private readonly AppConfig _config;
    private readonly bool _overlayTestOnly;
    private readonly bool _serviceBoot;
    private bool _tookOverFromExplorer;
    private SteamMonitor? _monitor;
    private SessionModes? _modes;
    private StartupAppWatcher? _startupWatcher;
    private OverlayController? _overlay;
    private TrayHost? _trayHost;
    private VolumeButtonService? _volumeButtons;
    private BootSplash? _splash;
    // Field-rooted deliberately: an unreferenced enabled FileSystemWatcher is
    // GC-collectible (it holds only a WeakReference to itself in its pending
    // ReadDirectoryChangesW state) and silently stops raising events.
    private System.IO.FileSystemWatcher? _configWatcher;
    private System.Threading.Timer? _configDebounce;
    private readonly object _configDebounceGate = new();

    /// <summary>Creates the shell session without performing any Windows state changes.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="overlayTestOnly">Whether to omit normal shell startup for the manual overlay test.</param>
    /// <param name="serviceBoot">Whether the logon service launched this process over a
    /// live, still-initializing explorer (--boot) — enables the takeover flow.</param>
    public ShellSession(AppConfig config, bool overlayTestOnly = false, bool serviceBoot = false)
    {
        _config = config;
        _overlayTestOnly = overlayTestOnly;
        _serviceBoot = serviceBoot;
    }

    /// <summary>Starts the shell's startup applications, home application, and overlay services.</summary>
    public void Start()
    {
        _monitor = new SteamMonitor();
        _modes = new SessionModes(_config, _monitor);
        _overlay = new OverlayController(_config, _monitor, _modes);

        // The tray host must never coexist with explorer's taskbar (Z-order war
        // over FindWindow — see TrayHost): gone before explorer starts, back
        // after game mode kills it. Apps re-home their icons on each side's
        // TaskbarCreated broadcast.
        _modes.DesktopModeStarting += () =>
        {
            _volumeButtons?.SetGameModeActive(false);
            _overlay?.AttachTrayHost(null);
            _trayHost?.Dispose();
            _trayHost = null;
        };
        _modes.GameModeEntered += () =>
        {
            _trayHost = TrayHost.Create();
            if (_trayHost is not null)
            {
                _overlay?.AttachTrayHost(_trayHost);
            }
            _volumeButtons?.SetGameModeActive(true);
        };

        if (_overlayTestOnly)
        {
            // Paused so a Steam exit can never trigger auto-relaunch/overlay-pop
            // reactions on a dev machine ("no apps started" contract); IsAlive
            // still updates for the HomeAppAlive display.
            _monitor.Paused = true;
            Log.Info("Overlay test mode (no apps started).");
            _overlay.ShowOverlay();
            return;
        }

        _volumeButtons = new VolumeButtonService(
            MessageWindow.Create(),
            () => DisplayScale.GetUiScalePercent(_config) / 100.0);

        // Refresh boot.json every session start so a stale Elevate/ExePath heals
        // itself before the next sign-in.
        BootManifestWriter.WriteCurrent(_config);

        // Service boot: the service launches WSGM at WTS_SESSION_LOGON — usually
        // BEFORE Winlogon has even started explorer (device-observed 2026-08-07:
        // gating this on IsRunningInSession made the takeover never run, leaving
        // explorer alive behind Big Picture next to our tray host). The takeover
        // owns every explorer state: its readiness poll waits for explorer to
        // appear AND finish logon prep, then shuts it down cleanly; if explorer
        // never shows within the 60 s cap it proceeds like a plain game-mode boot.
        if (_serviceBoot)
        {
            StartBootTakeover();
            return;
        }

        if (ExplorerControl.IsRunningInSession())
        {
            // A live desktop at --shell start means this is NOT a logon boot: it is
            // the update restart (updates only run in desktop mode) or a manual
            // start next to a desktop. Resume in desktop mode — no splash, no
            // startup apps, no Steam, no game posture/scale — with the overlay armed
            // so the panel is available; EnterGameMode brings everything back.
            Log.Info("Shell started with a live desktop — resuming in desktop mode (overlay armed).");
            _monitor.Paused = true;
            _startupWatcher = new StartupAppWatcher(_config.StartupApps);
            WatchConfig();
            return;
        }

        // Boot recomputes the posture value, so game mode re-applies it each start.
        // Posture first: it changes the display scale, and the splash sizes itself
        // to the final screen metrics.
        _modes.ApplyGameModePosture();
        _volumeButtons.SetGameModeActive(true);
        // Game-mode logon boot: host the tray now, before startup apps launch —
        // their Shell_NotifyIcon registrations need a living Shell_TrayWnd or
        // they only get an icon after the TaskbarCreated-driven retry (which
        // message-only tray windows never hear).
        _trayHost = TrayHost.Create();
        if (_trayHost is not null)
        {
            _overlay.AttachTrayHost(_trayHost);
        }
        if (_config.BootSplashEnabled)
        {
            _splash = new BootSplash(_config, _modes);
            _overlay.OverlayShown += () => _splash?.Dismiss("quick access opened");
            _splash.Show();
        }
        _startupWatcher = new StartupAppWatcher(_config.StartupApps);
        WatchConfig();

        Task.Run(async () =>
        {
            try
            {
                await LaunchAppsAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Shell session launch sequence failed", ex);
            }
            // Boot has settled (splash gone, apps and Steam up) — drop the
            // startup memory before the shell disappears behind the game.
            await Task.Delay(TimeSpan.FromSeconds(90));
            MemoryTrim.TrimBestEffort("boot settled");
        });
    }

    /// <summary>Service-boot takeover: cover the booting desktop with the splash
    /// FIRST (before any posture change — the cover is the point of the early
    /// launch), let explorer finish its logon prep once, then cleanly shut it down
    /// and run the normal game-mode boot. The one-per-session explorer init is what
    /// keeps touch features (touch keyboard) alive in game mode.</summary>
    private void StartBootTakeover()
    {
        Log.Info("Boot cover: waiting for explorer logon prep.");
        _tookOverFromExplorer = true;

        if (_config.BootSplashEnabled)
        {
            _splash = new BootSplash(_config, _modes!);
            _overlay!.OverlayShown += () => _splash?.Dismiss("quick access opened");
            _splash.Show();
        }
        else
        {
            Log.Info("Boot splash disabled — takeover runs uncovered.");
        }

        _startupWatcher = new StartupAppWatcher(_config.StartupApps);
        WatchConfig();

        // Mode switches must not race the takeover (the overlay is live behind the
        // splash and its Desktop button would start a second explorer transition).
        _modes!.BeginTransition();

        Task.Run(async () =>
        {
            try
            {
                await RunBootTakeoverAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Boot takeover failed", ex);
            }
            finally
            {
                _modes!.EndTransition();
            }
            await Task.Delay(TimeSpan.FromSeconds(90));
            MemoryTrim.TrimBestEffort("boot settled");
        });
    }

    private async Task RunBootTakeoverAsync()
    {
        // Input-desktop barrier (era-proven): WTS_SESSION_LOGON fires while the
        // Welcome screen still owns the input desktop — proceeding then starts
        // Steam audibly behind LogonUI. WTS_SESSION_DESKTOP_READY never arrives
        // on this hardware; polling for winsta0\Default is the working signal.
        var desktopWatch = System.Diagnostics.Stopwatch.StartNew();
        while (!InputDesktop.IsDefaultInputDesktop())
        {
            if (desktopWatch.Elapsed >= TimeSpan.FromSeconds(60))
            {
                Log.Warn("Input desktop never became winsta0\\Default within 60 s — proceeding anyway.");
                break;
            }
            await Task.Delay(250);
        }
        if (desktopWatch.ElapsedMilliseconds > 250)
        {
            Log.Info($"Interactive desktop ready after {desktopWatch.ElapsedMilliseconds} ms.");
        }

        var settleDuration = TimeSpan.FromMilliseconds(Math.Max(0, _config.ExplorerLogonSettleMs));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        System.Diagnostics.Stopwatch? settle = null;
        long shellSeenMs = -1, taskbarSeenMs = -1;

        while (true)
        {
            var shellWindow = NativeMethods.GetShellWindow() != 0;
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0;
            var bigPicture = WindowFinder.FindWindow(Steam.ProcessNames, Steam.BigPictureWindowClass) != 0;
            if (shellWindow && shellSeenMs < 0)
            {
                shellSeenMs = watch.ElapsedMilliseconds;
            }
            if (taskbar && taskbarSeenMs < 0)
            {
                taskbarSeenMs = watch.ElapsedMilliseconds;
            }

            var action = ExplorerReadiness.Decide(shellWindow, taskbar, bigPicture,
                watch.Elapsed, settle?.Elapsed, settleDuration, ExplorerReadiness.MaxWait);
            if (action == ExplorerReadinessAction.BeginSettle)
            {
                settle = System.Diagnostics.Stopwatch.StartNew();
                Log.Info($"Explorer readiness: shell window after {shellSeenMs} ms, " +
                         $"taskbar after {taskbarSeenMs} ms — settling {(int)settleDuration.TotalMilliseconds} ms.");
            }
            else if (action == ExplorerReadinessAction.ProceedAccelerated)
            {
                Log.Info("Big Picture appeared during boot cover — accelerating takeover (invariant 7).");
                break;
            }
            else if (action == ExplorerReadinessAction.ProceedTimeout)
            {
                Log.Warn($"Explorer readiness timeout after {(int)ExplorerReadiness.MaxWait.TotalSeconds} s — proceeding anyway.");
                break;
            }
            else if (action == ExplorerReadinessAction.Proceed)
            {
                break;
            }
            await Task.Delay(250);
        }

        // Already off the UI thread — the bounded exit wait never blocks the
        // splash's spinner/fade. Logs its own outcome.
        var exited = ExplorerControl.ExitExplorerAndWait(TimeSpan.FromSeconds(5));
        if (!exited && ExplorerControl.IsRunningInSession())
        {
            // Fail open (era-proven): never enter a half game mode next to a live
            // explorer. Resume like a desktop session — overlay armed, monitor
            // paused, no Steam start; the user can retry from quick access.
            Log.Warn("Boot takeover failed open — explorer preserved; resuming in desktop mode (overlay armed).");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _splash?.Dismiss("takeover failed open");
                if (_monitor is not null)
                {
                    _monitor.Paused = true;
                }
            });
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Same order as the direct game-mode boot: posture (scale) with the
            // splash re-covering on the display change, then the tray host —
            // explorer is verifiably gone, so Create() can't race a dying taskbar.
            _modes!.ApplyGameModePosture();
            _trayHost = TrayHost.Create();
            if (_trayHost is not null)
            {
                _overlay?.AttachTrayHost(_trayHost);
            }
            _volumeButtons?.SetGameModeActive(true);
        });

        await LaunchAppsAsync();
    }

    /// <summary>Name-based liveness check for the double-launch guard. Deliberately
    /// name-only (not full-path): MainModule of a cross-integrity process throws,
    /// and a same-named copy running from elsewhere still means the user's tool is
    /// up. Protocol/non-exe targets always report false.</summary>
    private static bool IsAppAlreadyRunning(string path)
    {
        try
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.SessionId == session)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Enumeration hiccups must not block the launch sequence.
        }
        return false;
    }

    private void WatchConfig()
    {
        try
        {
            _configWatcher = new System.IO.FileSystemWatcher(Log.Directory, "config.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName,
            };
            void Reload(object? _)
                => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var config = ConfigStore.Load();
                    _overlay?.ApplyConfig(config);
                    _startupWatcher?.Apply(config.StartupApps);
                });
            // Changed/Renamed fire on threadpool threads — the swap must be locked
            // so two near-simultaneous events can't both dispose the same timer and
            // orphan one that still fires.
            void Debounce()
            {
                lock (_configDebounceGate)
                {
                    _configDebounce?.Dispose();
                    _configDebounce = new System.Threading.Timer(Reload, null, 500, System.Threading.Timeout.Infinite);
                }
            }
            _configWatcher.Changed += (_, _) => Debounce();
            _configWatcher.Renamed += (_, _) => Debounce();
        }
        catch (Exception ex)
        {
            Log.Warn($"Config watcher not available: {ex.Message}");
        }
    }

    private async Task LaunchAppsAsync()
    {
        var haveApps = _config.StartupApps.Exists(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Path));
        if (haveApps && _config.StartupDelayMs > 0)
        {
            Log.Info($"Waiting {_config.StartupDelayMs} ms before the first startup app (boot settle).");
            await Task.Delay(_config.StartupDelayMs);
        }

        foreach (var app in _config.StartupApps)
        {
            if (!app.Enabled || string.IsNullOrWhiteSpace(app.Path))
            {
                continue;
            }
            // Explorer processed Run keys/Startup folder during the takeover's
            // settle window — tools registered in both places must not launch twice.
            if (_tookOverFromExplorer && IsAppAlreadyRunning(app.Path))
            {
                Log.Info($"Startup app already running (explorer autostart) — skipping: {app.Path}");
                continue;
            }
            Log.Info($"Starting startup app: {app.Path} {app.Args}{(app.Elevated ? " (elevated)" : "")}");
            AppLauncher.Start(app.Path, app.Args, app.Elevated);
            await Task.Delay(Math.Max(0, _config.StaggerDelayMs));
        }

        if (_config.SteamDelayMs > 0)
        {
            await Task.Delay(_config.SteamDelayMs);
        }

        // The splash's Switch-to-desktop (or the overlay's) may have fired while
        // this sequence was still sleeping — EnterDesktopMode paused the monitor,
        // and starting Big Picture now would slam it over the fresh desktop.
        if (_monitor is { Paused: true })
        {
            Log.Info("Skipping Steam start: desktop mode was requested during boot.");
            return;
        }


        // Shared start + warning flow (also behind the overlay's Steam button);
        // boot surfaces failures itself because this runs off the UI thread.
        // (steam://open/bigpicture adopts a Steam that explorer's own autostart
        // already brought up, so no duplicate check is needed for Steam itself.)
        var warning = _modes!.StartBigPicture();
        if (warning is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _splash?.Dismiss("Steam start warning");
                _overlay?.SetWarning(warning);
                _overlay?.ShowOverlay();
            });
        }
    }
}
