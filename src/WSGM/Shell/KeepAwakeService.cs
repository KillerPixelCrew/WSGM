using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Session-lifetime keep-awake coordinator ("standby wake lock"): a manual
/// hold toggled from the quick-access Power tab, plus an automatic hold while the
/// running Steam client reports an active download (polled over the CEF bridge, so a
/// disabled CEF integration simply leaves the automatic side inert). Each hold is its
/// own Windows power request, so <c>powercfg /requests</c> attributes them separately
/// on a device. Deliberately survives desktop/game mode switches — a download should
/// keep the handheld awake in both modes.</summary>
public sealed class KeepAwakeService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly WakeLock _manualStandbyLock =
        new("WSGM keep-awake (manual quick-access toggle)");
    private readonly WakeLock _manualDisplayLock =
        new("WSGM keep-display-on (manual quick-access toggle)",
            Interop.NativeMethods.PowerRequestDisplayRequired);
    private readonly WakeLock _downloadLock = new("WSGM keep-awake (Steam download in progress)");
    private readonly SteamMonitor? _monitor;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _manualGate = new();
    private ManualWakeMode _manualMode = ManualWakeMode.Off;
    private volatile bool _autoEnabled;
    private int _inactiveStreak;

    /// <summary>Raised (on an arbitrary thread) whenever a hold engages or drops.</summary>
    public event Action? StateChanged;

    /// <summary>The user's current manual wake mode.</summary>
    public ManualWakeMode ManualMode
    {
        get
        {
            lock (_manualGate)
            {
                return _manualMode;
            }
        }
    }

    /// <summary>Whether the automatic download hold is active.</summary>
    public bool DownloadHold => _downloadLock.IsHeld;

    private KeepAwakeService(SteamMonitor? monitor, bool autoEnabled)
    {
        _monitor = monitor;
        _autoEnabled = autoEnabled;
    }

    /// <summary>Starts the poll loop and returns the running service.</summary>
    /// <param name="monitor">The shared Steam lifecycle monitor; polls are skipped
    /// while it reports Steam dead. Null polls unconditionally.</param>
    /// <param name="autoEnabled">Initial <c>KeepAwakeDuringDownloads</c> setting.</param>
    public static KeepAwakeService StartNew(SteamMonitor? monitor, bool autoEnabled)
    {
        var service = new KeepAwakeService(monitor, autoEnabled);
        _ = Task.Run(service.RunAsync);
        return service;
    }

    /// <summary>Advances the manual mode one step: Off → Standby →
    /// Standby+Display → Off.</summary>
    public void CycleManualMode()
        => SetManualMode(ManualMode switch
        {
            ManualWakeMode.Off => ManualWakeMode.Standby,
            ManualWakeMode.Standby => ManualWakeMode.StandbyAndDisplay,
            _ => ManualWakeMode.Off,
        });

    /// <summary>Applies a manual wake mode (the quick-access cycle button).</summary>
    /// <param name="mode">The desired mode.</param>
    public void SetManualMode(ManualWakeMode mode)
    {
        lock (_manualGate)
        {
            if (mode == _manualMode)
            {
                return;
            }
            // Acquire before release so a Standby→Standby+Display step never has a
            // gap with no lock held. A failed acquire leaves the previous locks in
            // place and keeps the old mode — the UI stays truthful.
            if (mode != ManualWakeMode.Off && !_manualStandbyLock.Acquire())
            {
                return;
            }
            if (mode == ManualWakeMode.StandbyAndDisplay && !_manualDisplayLock.Acquire())
            {
                return;
            }
            if (mode != ManualWakeMode.StandbyAndDisplay)
            {
                _manualDisplayLock.Release();
            }
            if (mode == ManualWakeMode.Off)
            {
                _manualStandbyLock.Release();
            }
            _manualMode = mode;
            Log.Info($"Keep awake: manual mode {mode} (quick access).");
        }
        StateChanged?.Invoke();
    }

    /// <summary>Applies a reloaded configuration. Turning the automatic side off drops
    /// an engaged download hold immediately; the manual hold is unaffected.</summary>
    /// <param name="autoEnabled">The new <c>KeepAwakeDuringDownloads</c> setting.</param>
    public void ApplyConfig(bool autoEnabled)
    {
        _autoEnabled = autoEnabled;
        if (!autoEnabled && _downloadLock.IsHeld)
        {
            _downloadLock.Release();
            Interlocked.Exchange(ref _inactiveStreak, 0);
            Log.Info("Keep awake: download hold released (disabled in settings).");
            StateChanged?.Invoke();
        }
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_autoEnabled)
                {
                    await PollOnceAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Keep awake poll failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        var active = false;
        var detail = "Steam not running";
        if (_monitor is null || _monitor.IsAlive)
        {
            var overview = await SteamDownloads.QueryAsync(token).ConfigureAwait(false);
            if (overview is { } o)
            {
                active = o.Active;
                detail = o.Active
                    ? $"{o.State}, appid {o.AppId}, {o.NetworkBytesPerSecond / 1_000_000.0:0.0} MB/s"
                    : o.Paused ? $"{o.State}, paused" : o.State;
            }
            else
            {
                // Unreachable counts as an inactive sample: after the release streak
                // a closed/dead Steam drops the hold instead of pinning the device
                // awake forever.
                detail = "Steam client unreachable";
            }
        }

        var hadHold = _downloadLock.IsHeld;
        var (hold, streak) = KeepAwakeDecider.Next(hadHold, _inactiveStreak, active);
        Interlocked.Exchange(ref _inactiveStreak, streak);
        if (hold && !hadHold)
        {
            if (_downloadLock.Acquire())
            {
                Log.Info($"Keep awake: download hold acquired ({detail}).");
                StateChanged?.Invoke();
            }
        }
        else if (!hold && hadHold)
        {
            _downloadLock.Release();
            Log.Info($"Keep awake: download hold released ({detail}).");
            StateChanged?.Invoke();
        }
    }

    /// <summary>Stops the poll loop and drops both holds.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _manualStandbyLock.Dispose();
        _manualDisplayLock.Dispose();
        _downloadLock.Dispose();
    }
}
