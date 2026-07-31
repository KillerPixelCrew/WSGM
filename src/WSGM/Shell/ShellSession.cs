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
    private StartupAppWatcher? _startupWatcher;
    private OverlayController? _overlay;
    // Field-rooted deliberately: an unreferenced enabled FileSystemWatcher is
    // GC-collectible (it holds only a WeakReference to itself in its pending
    // ReadDirectoryChangesW state) and silently stops raising events.
    private System.IO.FileSystemWatcher? _configWatcher;
    private System.Threading.Timer? _configDebounce;
    private readonly object _configDebounceGate = new();

    public ShellSession(AppConfig config, bool overlayTestOnly = false)
    {
        _config = config;
        _overlayTestOnly = overlayTestOnly;
    }

    public void Start()
    {
        _monitor = new SteamMonitor();
        _overlay = new OverlayController(_config, _monitor);

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

        // Boot recomputes the posture value, so game mode re-applies it each start.
        SlateMode.ApplyGameMode();
        DisplayScale.ApplyGameMode(_config);
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

        if (!Steam.IsInstalled)
        {
            Log.Warn("Steam is not installed — showing overlay instead.");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlay?.SetWarning("Steam was not found on this PC. Install Steam — WSGM is Steam-exclusive.");
                _overlay?.ShowOverlay();
            });
            return;
        }

        Log.Info("Starting Steam Big Picture.");
        var result = Steam.LaunchBigPicture();
        if (!result.Started)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlay?.SetWarning("Couldn't start Steam Big Picture.");
                _overlay?.ShowOverlay();
            });
        }
    }
}
