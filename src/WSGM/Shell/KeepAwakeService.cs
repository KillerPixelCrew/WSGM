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
    // Guards every download-hold transition together with _autoEnabled and the
    // streak, so a config change and an in-flight poll cannot interleave.
    private readonly object _downloadGate = new();
    private ManualWakeMode _manualMode = ManualWakeMode.Off;
    private bool _autoEnabled;
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

    private bool AutoEnabled
    {
        get
        {
            lock (_downloadGate)
            {
                return _autoEnabled;
            }
        }
    }

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
        bool released;
        // Same gate as the poll's own decision: the flag write and the release must
        // not interleave with an in-flight poll's acquire, or a poll that started
        // before the disable could re-engage the hold behind it — and with the loop
        // then skipping polls entirely, nothing would ever release it again.
        lock (_downloadGate)
        {
            _autoEnabled = autoEnabled;
            released = !autoEnabled && _downloadLock.IsHeld;
            if (released)
            {
                _downloadLock.Release();
                _inactiveStreak = 0;
            }
        }
        if (released)
        {
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
                if (AutoEnabled)
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

        // The whole decision runs under the gate ApplyConfig also takes, and
        // re-reads _autoEnabled inside it: the CEF query above can take seconds,
        // and a disable that lands during it must win over this (now stale) sample.
        string? change = null;
        lock (_downloadGate)
        {
            var hadHold = _downloadLock.IsHeld;
            var (hold, streak) = KeepAwakeDecider.Next(hadHold, _inactiveStreak, active && _autoEnabled);
            _inactiveStreak = streak;
            if (hold && !hadHold)
            {
                if (_downloadLock.Acquire())
                {
                    change = $"acquired ({detail})";
                }
            }
            else if (!hold && hadHold)
            {
                _downloadLock.Release();
                change = $"released ({detail})";
            }
        }
        if (change is not null)
        {
            Log.Info($"Keep awake: download hold {change}.");
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
