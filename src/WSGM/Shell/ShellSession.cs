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
    private OverlayController? _overlay;

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
