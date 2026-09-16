using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Per-app auto-relaunch for startup tools, opt-in via
///     StartupAppConfig.AutoRelaunch — a crashed Handheld Companion otherwise leaves
///     the device without controller input. Process-name polling like SteamMonitor;
///     an app is only relaunched after it has been seen alive once, with a delay
///     before the restart and a cooldown so a crash-looping tool can't be spammed.
/// </summary>
public sealed class StartupAppWatcher : IDisposable
{
    private static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RelaunchCooldown = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _lifetime = new();

    // Keyed by full path so two configured apps sharing an exe basename don't
    // collide on one state.
    private readonly Dictionary<string, WatchState> _states = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _timer;
    private List<StartupAppConfig> _apps;
    private bool _disposed;
    private bool _pollInFlight;

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

    /// <summary>Session policy preventing a deliberately suspended integration from relaunching.</summary>
    internal Func<string, bool>? IsLaunchSuppressed { get; init; }

    /// <summary>Invalidates delayed relaunches across even a very short desktop takeover.</summary>
    internal Func<string, int>? LaunchGeneration { get; init; }

    /// <summary>Stops periodic process monitoring.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Replaces the monitored startup-program configuration.</summary>
    /// <param name="apps">The newly saved startup-program configuration.</param>
    public void Apply(List<StartupAppConfig> apps)
    {
        _apps = apps;
    }

    private void Poll()
    {
        if (_disposed || _pollInFlight)
        {
            return;
        }

        // Snapshot the watch list on the UI thread — Apply() replaces _apps wholesale
        // on a config reload, so the background probe must not enumerate it.
        var probes = new List<(string Path, string Name)>();
        foreach (var app in _apps)
        {
            if (IsLaunchSuppressed?.Invoke(app.Path) == true)
            {
                // Invalidate already-queued relaunch callbacks as well as future polls.
                _states.Remove(app.Path);
                continue;
            }

            if (!app.Enabled || !app.AutoRelaunch || app.Path.Length == 0 || AppLauncher.IsProtocol(app.Path))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(app.Path);
            if (name.Length == 0)
            {
                continue;
            }

            probes.Add((app.Path, name));
        }

        if (probes.Count == 0)
        {
            return;
        }

        _pollInFlight = true;
        // One process snapshot for every watched app, taken off the UI thread with only
        // the resulting booleans marshalled back, so the 16 ms gamepad poll and the
        // overlay animations never wait on it. All watcher state stays UI-thread owned
        // in Apply.
        var lifetime = _lifetime.Token;
        Log.Observe(Task.Run(() =>
        {
            var alive = new bool[probes.Count];
            HashSet<string>? running = null;
            try
            {
                var names = new string[probes.Count];
                for (var i = 0; i < probes.Count; i++)
                {
                    names[i] = probes[i].Name;
                }

                running = WindowFinder.FindRunningNames(names);
            }
            catch (Exception ex)
            {
                Log.Warn($"Startup app liveness poll failed: {ex.Message}");
            }

            for (var i = 0; i < probes.Count; i++)
            {
                // An unknown result must never read as a crash: a failed probe
                // would otherwise relaunch an app that is still running.
                alive[i] = running?.Contains(probes[i].Name) ?? true;
            }

            if (!lifetime.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!lifetime.IsCancellationRequested)
                    {
                        Apply(probes, alive);
                    }
                });
            }
        }, lifetime), "startup app liveness poll");
    }

    private void Apply(List<(string Path, string Name)> probes, bool[] alive)
    {
        _pollInFlight = false;
        for (var i = 0; i < probes.Count; i++)
        {
            var (path, name) = probes[i];
            if (IsLaunchSuppressed?.Invoke(path) == true)
            {
                _states.Remove(path);
                continue;
            }

            var generation = LaunchGeneration?.Invoke(path) ?? 0;
            if (!_states.TryGetValue(path, out var state) || state.LaunchGeneration != generation)
            {
                state = new WatchState { LaunchGeneration = generation };
                _states[path] = state;
            }

            // The new state is always recorded, even while a relaunch is pending —
            // only the reaction is gated.
            var exited = state.WasAlive && !alive[i];
            state.WasAlive = alive[i];
            if (!exited || state.RelaunchPending)
            {
                continue;
            }

            // A falling edge inside the cooldown isn't dropped — the relaunch is
            // scheduled for when the cooldown expires (never sooner than the
            // normal delay).
            var remaining = state.LastRelaunchUtc + RelaunchCooldown - DateTime.UtcNow;
            var delay = remaining > RelaunchDelay ? remaining : RelaunchDelay;
            state.RelaunchPending = true;
            Log.Info($"Startup app '{name}' exited — relaunching in {delay.TotalSeconds:0} s.");
            Log.Observe(
                RelaunchAfterDelayAsync(path, name, state, delay, _lifetime.Token),
                $"startup app relaunch for {name}");
        }
    }

    private async Task RelaunchAfterDelayAsync(
        string path,
        string name,
        WatchState state,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    // The state and current configuration remain UI-thread owned.
                    Relaunch(path, name, state);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     Fires a scheduled relaunch. The app is re-resolved from the CURRENT
    ///     config here — a reload during the delay window may have removed or disabled
    ///     it, and stale captured path/args must not win over the user's edit.
    /// </summary>
    private void Relaunch(string path, string name, WatchState state)
    {
        state.RelaunchPending = false;
        if (!_states.TryGetValue(path, out var current) || !ReferenceEquals(current, state)
                                                        || state.LaunchGeneration !=
                                                        (LaunchGeneration?.Invoke(path) ?? 0))
        {
            return;
        }

        var app = _apps.Find(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        if (app is null || !app.Enabled || !app.AutoRelaunch || IsLaunchSuppressed?.Invoke(path) == true)
        {
            Log.Info($"Startup app '{name}' relaunch skipped — removed or disabled meanwhile.");
            return;
        }

        state.LastRelaunchUtc = DateTime.UtcNow;
        AppLauncher.Start(app.Path, app.Args, app.Elevated);
    }

    private sealed class WatchState
    {
        public DateTime LastRelaunchUtc;
        public int LaunchGeneration;

        public bool RelaunchPending;

        // Only true after a poll saw the process alive, so "seen alive once" before a
        // relaunch is implied rather than tracked separately.
        public bool WasAlive;
    }
}
