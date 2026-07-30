using System;
using System.Threading.Tasks;
using OpenFSE.Core;

namespace OpenFSE.Shell;

/// <summary>Shell-mode orchestrator: starts startup apps and the home app, stays
/// resident for the whole session. Overlay wiring arrives in M2.</summary>
public sealed class ShellSession
{
    private readonly AppConfig _config;
    private readonly bool _overlayTestOnly;

    public ShellSession(AppConfig config, bool overlayTestOnly = false)
    {
        _config = config;
        _overlayTestOnly = overlayTestOnly;
    }

    public void Start()
    {
        if (_overlayTestOnly)
        {
            Log.Info("Overlay test mode (no apps started). Overlay arrives in M2.");
            // M2: OverlayController.ShowOverlay();
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

        // M2: arm OverlayController (hotkey + edge strips) + HomeAppMonitor here.
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
            Log.Warn("No home app configured.");
            return;
        }

        Log.Info($"Starting home app: {home.Path} {home.Args}{(home.Elevated ? " (elevated)" : "")}");
        var result = AppLauncher.Start(home.Path, home.Args, home.Elevated);
        if (result.ElevationDeclined)
        {
            Log.Warn("Home app is running WITHOUT elevation (declined).");
        }
    }
}
