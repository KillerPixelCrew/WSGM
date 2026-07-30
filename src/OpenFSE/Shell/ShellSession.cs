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
    private HomeAppMonitor? _monitor;
    private OverlayController? _overlay;

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

        Log.Info($"Starting home app: {home.Path} {home.Args}{(home.Elevated ? " (elevated)" : "")}");
        var result = AppLauncher.Start(home.Path, home.Args, home.Elevated);
        if (result.ElevationDeclined)
        {
            _overlay?.SetWarning("Home app started WITHOUT elevation (UAC declined).");
        }
    }
}
