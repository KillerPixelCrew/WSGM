using System;
using System.Collections.Generic;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Per-app auto-relaunch for startup tools, opt-in via
/// StartupAppConfig.AutoRelaunch — a crashed Handheld Companion otherwise leaves
/// the device without controller input. Process-name polling like SteamMonitor;
/// an app is only relaunched after it has been seen alive once, with a delay
/// before the restart and a cooldown so a crash-looping tool can't be spammed.</summary>
public sealed class StartupAppWatcher : IDisposable
{
    private static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RelaunchCooldown = TimeSpan.FromSeconds(30);

    private sealed class WatchState
    {
        public readonly AliveEdgeDetector Edge = new();
        public DateTime LastRelaunchUtc;
        public bool RelaunchPending;
    }

    private readonly DispatcherTimer _timer;
    private List<StartupAppConfig> _apps;
    // Keyed by full path so two configured apps sharing an exe basename don't
    // collide on one state.
    private readonly Dictionary<string, WatchState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a watcher for the currently configured startup programs.</summary>
    /// <param name="apps">The startup-program configuration to monitor.</param>
    public StartupAppWatcher(List<StartupAppConfig> apps)
    {
        _apps = apps;
        // The convenience ctor taking a callback auto-starts the timer (see
        // GamepadService) — keep construction and Start() explicit.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>Replaces the monitored startup-program configuration.</summary>
    /// <param name="apps">The newly saved startup-program configuration.</param>
    public void Apply(List<StartupAppConfig> apps) => _apps = apps;

    private void Poll()
    {
        foreach (var app in _apps)
        {
            if (!app.Enabled || !app.AutoRelaunch || app.Path.Length == 0 || AppLauncher.IsProtocol(app.Path))
            {
                continue;
            }
            var name = System.IO.Path.GetFileNameWithoutExtension(app.Path);
            if (name.Length == 0)
            {
                continue;
            }
            if (!_states.TryGetValue(app.Path, out var state))
            {
                state = new WatchState();
                _states[app.Path] = state;
            }

            var alive = WindowFinder.FindProcessIds(name).Count > 0;

            // Update() always records the new state, even while a relaunch is
            // pending — only the reaction is gated, matching the old
            // WasAlive bookkeeping.
            if (state.Edge.Update(alive) && !state.RelaunchPending)
            {
                // A falling edge inside the cooldown isn't dropped — the relaunch is
                // scheduled for when the cooldown expires (never sooner than the
                // normal delay).
                var remaining = state.LastRelaunchUtc + RelaunchCooldown - DateTime.UtcNow;
                var delay = remaining > RelaunchDelay ? remaining : RelaunchDelay;
                state.RelaunchPending = true;
                Log.Info($"Startup app '{name}' exited — relaunching in {delay.TotalSeconds:0} s.");
                var path = app.Path;
                System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ =>
                    Dispatcher.UIThread.Post(() => Relaunch(path, name, state)));
            }
        }
    }

    /// <summary>Fires a scheduled relaunch. The app is re-resolved from the CURRENT
    /// config here — a reload during the delay window may have removed or disabled
    /// it, and stale captured path/args must not win over the user's edit.</summary>
    private void Relaunch(string path, string name, WatchState state)
    {
        state.RelaunchPending = false;
        var app = _apps.Find(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        if (app is null || !app.Enabled || !app.AutoRelaunch)
        {
            Log.Info($"Startup app '{name}' relaunch skipped — removed or disabled meanwhile.");
            return;
        }
        state.LastRelaunchUtc = DateTime.UtcNow;
        AppLauncher.Start(app.Path, app.Args, app.Elevated);
    }

    /// <summary>Stops periodic process monitoring.</summary>
    public void Dispose() => _timer.Stop();
}
