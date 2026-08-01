using System;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Shell-mode orchestrator: starts startup apps and the home app, arms the
/// overlay (hotkey + edge swipes + home-exit), stays resident for the session.</summary>
public sealed class ShellSession
{
    private readonly AppConfig _config;
    private readonly bool _overlayTestOnly;
    private SteamMonitor? _monitor;
    private SessionModes? _modes;
    private StartupAppWatcher? _startupWatcher;
    private OverlayController? _overlay;
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
    public ShellSession(AppConfig config, bool overlayTestOnly = false)
    {
        _config = config;
        _overlayTestOnly = overlayTestOnly;
    }

    /// <summary>Starts the shell's startup applications, home application, and overlay services.</summary>
    public void Start()
    {
        _monitor = new SteamMonitor();
        _modes = new SessionModes(_config, _monitor);
        _overlay = new OverlayController(_config, _monitor, _modes);

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

        // A live desktop at shell start means this is NOT a logon boot: it is the
        // update restart (updates only run in desktop mode) or an AutoRestartShell
        // resurrection next to a desktop. Resume in desktop mode — no splash, no
        // startup apps, no Steam, no game posture/scale — with the overlay armed
        // so the panel is available; EnterGameMode brings everything back.
        if (ExplorerControl.IsRunningInSession())
        {
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
