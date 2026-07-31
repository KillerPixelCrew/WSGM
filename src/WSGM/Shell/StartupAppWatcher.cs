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
        public bool WasAlive;
        public bool EverAlive;
        public DateTime LastRelaunchUtc;
        public bool RelaunchPending;
    }

    private readonly DispatcherTimer _timer;
    private List<StartupAppConfig> _apps;
    private readonly Dictionary<string, WatchState> _states = new(StringComparer.OrdinalIgnoreCase);

    public StartupAppWatcher(List<StartupAppConfig> apps)
    {
        _apps = apps;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Poll());
        _timer.Start();
    }

    public void Apply(List<StartupAppConfig> apps) => _apps = apps;

    private void Poll()
    {
        foreach (var app in _apps)
        {
            if (!app.Enabled || !app.AutoRelaunch || app.Path.Length == 0 || app.Path.Contains("://"))
            {
                continue;
            }
            var name = System.IO.Path.GetFileNameWithoutExtension(app.Path);
            if (name.Length == 0)
            {
                continue;
            }
            if (!_states.TryGetValue(name, out var state))
            {
                state = new WatchState();
                _states[name] = state;
            }

            var alive = WindowFinder.FindProcessIds(name).Count > 0;
            if (alive)
            {
                state.EverAlive = true;
            }

            if (state.WasAlive && !alive && state.EverAlive && !state.RelaunchPending
                && DateTime.UtcNow - state.LastRelaunchUtc > RelaunchCooldown)
            {
                state.RelaunchPending = true;
                state.LastRelaunchUtc = DateTime.UtcNow;
                Log.Info($"Startup app '{name}' exited — relaunching in {RelaunchDelay.TotalSeconds:0} s.");
                var path = app.Path;
                var args = app.Args;
                var elevated = app.Elevated;
                System.Threading.Tasks.Task.Delay(RelaunchDelay).ContinueWith(_ =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        state.RelaunchPending = false;
                        AppLauncher.Start(path, args, elevated);
                    }));
            }
            state.WasAlive = alive;
        }
    }

    public void Dispose() => _timer.Stop();
}
