using System;
using System.Threading.Tasks;
using OpenFSE.Core;
using OpenFSE.Overlay;

namespace OpenFSE.Shell;

/// <summary>Shell-mode orchestrator: starts startup apps and the home app, arms the
/// overlay (hotkey + edge swipes + home-exit), stays resident for the session.</summary>
public sealed class ShellSession
{
    private readonly AppConfig _config;
    private readonly bool _overlayTestOnly;
    private readonly object _homeLaunchGate = new();
    private HomeAppMonitor? _monitor;
    private OverlayController? _overlay;
    private bool _homeLaunchInProgress;
    private DateTime _lastHomeLaunchUtc;

    private static readonly TimeSpan HomeLaunchCooldown = TimeSpan.FromSeconds(5);

    public ShellSession(AppConfig config, bool overlayTestOnly = false)
    {
        _config = config;
        _overlayTestOnly = overlayTestOnly;
    }

    public void Start()
    {
        _monitor = new HomeAppMonitor(_config.HomeApp);
        _overlay = new OverlayController(_config, _monitor);

        if (_overlayTestOnly)
        {
            Log.Info("Overlay test mode (no apps started).");
            _overlay.ShowOverlay();
            return;
        }

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
            var watcher = new System.IO.FileSystemWatcher(Log.Directory, "config.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName,
            };
            System.Threading.Timer? debounce = null;
            void Reload(object? _)
                => Avalonia.Threading.Dispatcher.UIThread.Post(() => _overlay?.ApplyConfig(ConfigStore.Load()));
            watcher.Changed += (_, _) =>
            {
                debounce?.Dispose();
                debounce = new System.Threading.Timer(Reload, null, 500, System.Threading.Timeout.Infinite);
            };
            watcher.Renamed += (_, _) =>
            {
                debounce?.Dispose();
                debounce = new System.Threading.Timer(Reload, null, 500, System.Threading.Timeout.Infinite);
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Config watcher not available: {ex.Message}");
        }
    }

    private async Task LaunchAppsAsync()
    {
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

        if (_config.HomeAppDelayMs > 0)
        {
            await Task.Delay(_config.HomeAppDelayMs);
        }

        var home = _config.HomeApp;
        if (string.IsNullOrWhiteSpace(home.Path))
        {
            Log.Warn("No home app configured — showing overlay instead.");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _overlay?.ShowOverlay());
            return;
        }

        var result = StartHomeApp(home);
        if (result is null)
        {
            return;
        }
        if (!result.Started)
        {
            Log.Error($"Home app failed to start: {home.Path}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlay?.SetWarning($"Couldn't start {System.IO.Path.GetFileNameWithoutExtension(home.Path)}. Check its path and permissions.");
                _overlay?.ShowOverlay();
            });
            return;
        }
        if (result.ElevationDeclined)
        {
            _overlay?.SetWarning("Home app started WITHOUT elevation (UAC declined).");
        }
    }

    private AppLauncher.LaunchResult? StartHomeApp(HomeAppConfig home)
    {
        lock (_homeLaunchGate)
        {
            if (_homeLaunchInProgress || DateTime.UtcNow - _lastHomeLaunchUtc < HomeLaunchCooldown)
            {
                Log.Warn("Skipping duplicate home-app start request.");
                return null;
            }
            _homeLaunchInProgress = true;
        }

        try
        {
            Log.Info($"Starting home app: {home.Path} {home.Args}{(home.Elevated ? " (elevated)" : "")}");
            return AppLauncher.Start(home.Path, home.Args, home.Elevated);
        }
        finally
        {
            lock (_homeLaunchGate)
            {
                _homeLaunchInProgress = false;
                _lastHomeLaunchUtc = DateTime.UtcNow;
            }
        }
    }
}
