using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;

namespace WSGM.Shell;

/// <summary>
///     Owns UI-bound Wi-Fi and Bluetooth state, discovery and user actions for the session.
/// </summary>
/// <remarks>
///     Invoke lifecycle and row-mutating actions on the Avalonia UI thread. Blocking native work runs
///     on workers and watch callbacks post updates to the dispatcher. Collections retain surviving row
///     instances to preserve focus. Dispose stops this manager's timer and watches and ends its pairing attempt.
/// </remarks>
public sealed class RadioManager : ObservableObject, IDisposable
{
    /// <summary>
    ///     Containers with Bluetooth audio endpoints, mapped to whether
    ///     those endpoints are live. The devices whose rows get a
    ///     Connect/Disconnect action, and which way round it reads. Refreshed with
    ///     each panel-open snapshot.
    /// </summary>
    private readonly Dictionary<string, bool> _audioContainers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The endpoint census and canonical logical Bluetooth identities.</summary>
    private readonly BluetoothDeviceCatalog _bluetoothCatalog = new();

    private readonly SemaphoreSlim _bluetoothPowerGate = new(1, 1);

    private readonly Action<string, bool> _connectBluetoothAudio;

    private readonly
        Func<string, Action<WindowsRadio.PairingRequest>, CancellationToken, Task<WindowsRadio.PairingResult>>
        _pairBluetooth;

    // One gate per radio: a Wi-Fi toggle must not wait behind a Bluetooth one.
    private readonly SemaphoreSlim _wifiPowerGate = new(1, 1);
    private bool _accessLogged;

    /// <summary>
    ///     This manager's Bluetooth watch while the feeds run. Every manager owns its own, so the
    ///     Settings preview's sheet and the session's never stop each other's discovery. Disposing it
    ///     is the stop. UI thread only.
    /// </summary>
    private IDisposable? _bluetoothWatch;

    /// <summary>Non-zero while a connection attempt is in flight.</summary>
    private int _connecting;

    private volatile bool _disposed;

    private bool _feedsStarted;

    /// <summary>
    ///     Ends the running pairing attempt when the manager is disposed. A user cancel declines the
    ///     pending question instead, so it ends with Windows' own cancelled outcome and text.
    /// </summary>
    private CancellationTokenSource? _pairingCancellation;

    private bool _pairingCancelled;

    private string? _pairingEndpointId;
    private BluetoothDeviceEntry? _pairingEntry;

    private bool _pairingInProgress;
    private uint _pairingToken;
    private bool _panelScanning;
    private int _refreshing;
    private bool _scanning;

    /// <summary>
    ///     Whether <see cref="StatusText" /> currently holds a scan
    ///     failure, and may therefore be cleared once scanning recovers.
    /// </summary>
    private bool _statusIsScanFailure;

    private bool _steamScanning;

    private DispatcherTimer? _timer;

    /// <summary>This manager's Wi-Fi watch once it has started. UI thread only.</summary>
    private IDisposable? _wifiWatch;

    /// <summary>Creates the session radio manager over the Windows backends.</summary>
    public RadioManager() : this(WindowsRadio.PairBluetoothAsync, CoreAudio.SetBluetoothAudioConnection)
    {
    }

    /// <summary>Creates an inert manager with replaceable pairing and audio-connection backends.</summary>
    /// <param name="pairBluetooth">Starts one bounded pairing operation and invokes ceremony callbacks from a Windows thread.</param>
    /// <param name="connectBluetoothAudio">Synchronous audio connect/disconnect request, invoked on a worker.</param>
    internal RadioManager(
        Func<string, Action<WindowsRadio.PairingRequest>, CancellationToken, Task<WindowsRadio.PairingResult>>
            pairBluetooth,
        Action<string, bool> connectBluetoothAudio)
    {
        _pairBluetooth = pairBluetooth;
        _connectBluetoothAudio = connectBluetoothAudio;
    }

    /// <summary>Gets the Wi-Fi networks in range, strongest first.</summary>
    public ObservableCollection<WifiNetworkEntry> Networks { get; } = [];

    /// <summary>Gets the Bluetooth devices that are paired or visible.</summary>
    public ObservableCollection<BluetoothDeviceEntry> BluetoothDevices { get; } = [];

    /// <summary>Gets the Wi-Fi radio's power state.</summary>
    public RadioPower WifiPower
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(WifiPower)))
            {
                return;
            }

            Raise(nameof(WifiOn));
            Raise(nameof(WifiUnavailableText));
            Raise(nameof(WifiIconState));
        }
    } = RadioPower.Unknown;

    /// <summary>Gets the Bluetooth radio's power state.</summary>
    public RadioPower BluetoothPower
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(BluetoothPower)))
            {
                return;
            }

            Raise(nameof(BluetoothOn));
            Raise(nameof(BluetoothUnavailableText));
            Raise(nameof(BluetoothIconState));
        }
    } = RadioPower.Unknown;

    /// <summary>Gets whether the Wi-Fi radio is on.</summary>
    public bool WifiOn => WifiPower == RadioPower.On;

    /// <summary>Gets whether the Bluetooth radio is on.</summary>
    public bool BluetoothOn => BluetoothPower == RadioPower.On;

    /// <summary>
    ///     Gets what to tell the user when the Wi-Fi list is empty because
    ///     the radio is not usable. "Off" is only one of the reasons, and the least
    ///     alarming: a blocked or missing adapter cannot be switched on at all, and
    ///     saying "off" leaves the user pressing a dead switch.
    /// </summary>
    public string WifiUnavailableText => DescribeUnavailable(WifiPower, "Wi-Fi");

    /// <summary>Gets the same explanation for Bluetooth.</summary>
    public string BluetoothUnavailableText => DescribeUnavailable(BluetoothPower, "Bluetooth");

    /// <summary>
    ///     Gets what the sheet's Wi-Fi pill should show. Off and merely
    ///     disconnected are different problems and must not look the same.
    /// </summary>
    public RadioIconState WifiIconState => WifiPower switch
    {
        RadioPower.On when WifiConnected => RadioIconState.Connected,
        RadioPower.On => RadioIconState.Disconnected,
        _ => RadioIconState.Off
    };

    /// <summary>
    ///     Gets what the sheet's Bluetooth pill should show. Accent only
    ///     when a device is actually connected — a lone powered radio is
    ///     "disconnected", the same distinction the Wi-Fi pill draws.
    /// </summary>
    public RadioIconState BluetoothIconState => BluetoothPower switch
    {
        RadioPower.On when BluetoothConnectedCount > 0 => RadioIconState.Connected,
        RadioPower.On => RadioIconState.Disconnected,
        _ => RadioIconState.Off
    };

    /// <summary>
    ///     Gets how many Bluetooth devices have a live connection. Read
    ///     from PnP state every status tick, so the pill is correct whether or not
    ///     the panel has ever been opened.
    /// </summary>
    public int BluetoothConnectedCount
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(BluetoothConnectedCount)))
            {
                return;
            }

            Raise(nameof(BluetoothIconState));
        }
    }

    /// <summary>
    ///     Gets whether Wi-Fi is joined to a network — the only state that
    ///     tints the sheet's Wi-Fi pill with the accent color.
    /// </summary>
    public bool WifiConnected
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(WifiConnected)))
            {
                return;
            }

            Raise(nameof(WifiIconState));
        }
    }

    /// <summary>
    ///     Gets the joined network's signal quality, 0-100. Drives the bars
    ///     on the sheet's Wi-Fi pill.
    /// </summary>
    public int WifiSignal
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(WifiSignal));
    }

    /// <summary>Gets the joined network's name, or an empty string.</summary>
    public string ConnectedSsid
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(ConnectedSsid));
    } = "";

    /// <summary>Gets the joined network's identity, or null when Wi-Fi is not joined to one.</summary>
    internal WindowsRadio.WifiNetworkKey? ConnectedNetwork { get; private set; }

    /// <summary>
    ///     Gets the last thing that happened, for the panel's status line.
    ///     Empty when there is nothing to report.
    /// </summary>
    public string StatusText
    {
        get;
        private set
        {
            // Any writer takes ownership of the message; only Apply's own scan
            // branch re-claims it, so a connect or pairing result is never
            // cleared by an unrelated successful scan.
            _statusIsScanFailure = false;
            if (!SetFieldIfChanged(ref field, value, nameof(StatusText)))
            {
                return;
            }

            Raise(nameof(HasStatus));
        }
    } = "";

    /// <summary>Gets whether a status line should be shown.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>
    ///     Gets whether a Bluetooth sweep is still running, so the panel can
    ///     show that more devices may still appear.
    /// </summary>
    public bool BluetoothScanning
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(BluetoothScanning));
    }

    /// <summary>
    ///     Stops the update timer. Idempotent; bound values keep their last
    ///     state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopScanning();
        if (_pairingInProgress)
        {
            if (_pairingToken != 0)
            {
                RespondToPairing(_pairingToken, false, null);
            }

            // On the thread pool, like every pairing answer: completing a pairing deferral on the
            // UI thread's STA can wedge the Device Association service.
            _ = _pairingCancellation?.CancelAsync();
            FinishPairing();
        }

        PairingRequested = null;
        PairingFinished = null;
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>
    ///     Raised when Windows asks a pairing question and the UI must
    ///     answer with <see cref="RespondToPairing" />. Always on the UI thread.
    /// </summary>
    public event Action<PairingPrompt>? PairingRequested;

    /// <summary>
    ///     Raised when a pairing attempt finishes, with a message to show.
    ///     Always on the UI thread.
    /// </summary>
    public event Action<string>? PairingFinished;

    /// <summary>
    ///     The reason a radio is not usable, named rather than flattened
    ///     into "off".
    /// </summary>
    /// <param name="power">The radio's power state.</param>
    /// <param name="label">The radio's display name.</param>
    /// <returns>An unavailable-state message, or empty for an on/unclassified enum value.</returns>
    internal static string DescribeUnavailable(RadioPower power, string label)
    {
        return power switch
        {
            RadioPower.Off => $"{label} is off.",
            RadioPower.Disabled => $"{label} is blocked by Windows or a hardware switch.",
            RadioPower.Absent => $"This device has no {label} adapter.",
            RadioPower.Unknown => $"{label} state is unavailable.",
            _ => ""
        };
    }

    /// <summary>
    ///     Performs a first refresh and starts the update timer.
    ///     UI-thread callers only. Idempotent.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The manager has been disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer is not null)
        {
            return;
        }

        QueueRefresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    ///     Stops the update timer without disposing the manager, so a later <see cref="Start" /> can
    ///     resume it. UI-thread callers only. Idempotent.
    /// </summary>
    /// <remarks>Does not stop discovery feeds or an already-running refresh.</remarks>
    public void Stop()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>
    ///     Begins actively scanning for networks and devices. Called when
    ///     the radio panel opens: an idle sheet must not pay for scans nobody is
    ///     looking at, which on a handheld is battery.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The manager has been disposed.</exception>
    public void StartScanning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _panelScanning = true;
        UpdateScanning();
    }

    /// <summary>Updates Steam's discovery demand on the UI thread; panel or pairing demand may keep feeds running.</summary>
    /// <param name="enabled">Whether Steam currently needs live discovery; ignored after disposal.</param>
    internal void SetSteamDiscovery(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _steamScanning = enabled;
        UpdateScanning();
    }

    private void UpdateScanning()
    {
        var wanted = !_disposed && (_panelScanning || _steamScanning || _pairingInProgress);
        if (!wanted)
        {
            _scanning = false;
            StopFeeds();
            BluetoothScanning = false;
            return;
        }

        if (_scanning)
        {
            return;
        }

        _scanning = true;
        Log.Info("Radio panel: scanning started.");
        // Publish the cached scan list immediately — it is already there and
        // costs milliseconds — then ask for a fresh scan and let the live feeds
        // fill in the rest.
        QueueRefresh();
        StartFeeds();
        Rescan();
    }

    /// <summary>Stops actively scanning. Idempotent.</summary>
    public void StopScanning()
    {
        _panelScanning = false;
        UpdateScanning();
    }

    /// <summary>
    ///     Asks for a fresh sweep of both radios.
    ///     Bound to the panel's refresh button: without it the only way to look for
    ///     a network or a device that appeared after opening was to close and reopen
    ///     the panel.
    /// </summary>
    public void Rescan()
    {
        Log.Info("Radio panel: rescan requested.");
        StatusText = "";
        // Only when a watcher restart is really queued: with the feeds stopped nothing would ever
        // clear the flag, and the scanning spinner would stay.
        if (BluetoothPower == RadioPower.On && _feedsStarted)
        {
            BluetoothScanning = true;
            // A fresh sweep starts a fresh census; stale rows are dropped when
            // it completes.
            _bluetoothCatalog.BeginSweep();
            // Restarting the watcher re-runs the initial enumeration, which is
            // what picks up a device that has only just been put into pairing
            // mode. Existing rows survive because they are matched by id.
            RestartBluetoothFeed();
        }

        _ = Task.Run(() =>
        {
            try
            {
                WindowsRadio.RequestWifiScan();
            }
            catch (Exception ex)
            {
                // Nothing awaits this task, so an unobserved throw would only
                // surface at an arbitrary later finalization — if ever.
                Log.Warn($"Wi-Fi scan request threw: {ex.Message}");
            }
        });
        QueueRefresh();
    }

    private void RestartBluetoothFeed()
    {
        if (!_feedsStarted)
        {
            return;
        }

        StopBluetoothFeed();
        StartBluetoothFeed();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // A safety net only: the live feeds carry every real change, so this is
        // here to catch a driver that stops reporting, not to drive the UI.
        QueueRefresh();
    }

    /// <summary>
    ///     Refreshes state off the UI thread, at most one at a time. A slow
    ///     Windows call must not queue up behind itself every tick.
    /// </summary>
    private void QueueRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = ReadSnapshot(_scanning);
                Dispatcher.UIThread.Post(() => Apply(snapshot));
            }
            catch (Exception ex)
            {
                Log.Warn($"Radio refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private static Snapshot ReadSnapshot(bool includeNetworks)
    {
        var wifiPower = ReadPower(WindowsRadio.RadioKind.WiFi);
        var bluetoothPower = ReadPower(WindowsRadio.RadioKind.Bluetooth);

        // Answered from PnP state, no inquiry — cheap enough for every tick,
        // and the only way the tile can distinguish "on" from "connected"
        // without the panel's watcher running.
        var bluetoothConnected = 0;
        if (bluetoothPower == RadioPower.On)
        {
            try
            {
                bluetoothConnected = WindowsRadio.ConnectedBluetoothCount();
            }
            catch (Exception ex)
            {
                Log.Change("radio-bluetooth-connected-query",
                    $"Bluetooth connected-device query unavailable: {ex.Message}");
            }
        }

        // State, signal and SSID together, every tick: reading the signal only
        // while the panel was open left the Wi-Fi pill with no bars until the
        // panel had been opened once.
        var wifi = ReadWifiStatus();

        IReadOnlyList<WindowsRadio.WifiNetwork> networks = [];
        string? failure = null;
        var failureStatus = 0;
        // Only a SUCCESSFUL listing counts as carrying a network list: a failed
        // one would make Apply reconcile against an empty collection and wipe
        // every row over a transient WLAN-service error.
        var listed = false;

        if (includeNetworks && wifiPower == RadioPower.On)
        {
            try
            {
                networks = WindowsRadio.ListWifiNetworks();
                listed = true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                // The WLAN status, which is what names the location-consent gate.
                failureStatus = ex is Win32Exception native ? native.NativeErrorCode : 0;
            }
        }

        // Only while the panel is open: the audio-endpoint set decides which
        // Bluetooth rows get a Connect action, and only the panel shows rows.
        // Local PnP enumeration, no radio traffic.
        var audio = includeNetworks ? QueryBluetoothAudioContainers() : null;

        return new Snapshot(
            wifiPower,
            bluetoothPower,
            bluetoothConnected,
            wifi,
            listed,
            networks,
            audio,
            failure,
            failureStatus);
    }

    /// <summary>Reads the Wi-Fi interface status, or an unknown, unjoined one when it cannot be read.</summary>
    private static WindowsRadio.WifiStatus ReadWifiStatus()
    {
        try
        {
            return WindowsRadio.GetWifiStatus();
        }
        catch (Exception ex)
        {
            Log.Change("radio-wifi-status-query",
                $"Wi-Fi status query unavailable: {ex.Message}");
            return new WindowsRadio.WifiStatus(WindowsRadio.WifiConnectionState.Unknown, 0, "", null);
        }
    }

    /// <summary>
    ///     Reads the Wi-Fi status off the UI thread and publishes it, for a consumer that needs it
    ///     current while the refresh timer is stopped.
    /// </summary>
    /// <returns>Completes after the worker read and UI publication; disposal suppresses publication and read failures publish unknown state.</returns>
    internal async Task RefreshWifiStatusAsync()
    {
        var status = await Task.Run(ReadWifiStatus).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                ApplyWifiStatus(status);
            }
        });
    }

    private void ApplyWifiStatus(WindowsRadio.WifiStatus status)
    {
        WifiConnected = status.State == WindowsRadio.WifiConnectionState.Connected;
        // Straight from the interface, so the pill has bars whether or not the
        // panel has ever been opened.
        WifiSignal = status.Signal;
        ConnectedSsid = status.Ssid;
        ConnectedNetwork = status.Key;
    }

    private static IReadOnlyList<CoreAudio.BluetoothAudioContainer>? QueryBluetoothAudioContainers()
    {
        try
        {
            return CoreAudio.ListBluetoothAudioContainers();
        }
        catch (Exception ex)
        {
            Log.Warn($"Bluetooth audio endpoint query failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Applies the audio-endpoint facts to one row.</summary>
    private void ApplyAudioState(BluetoothDeviceEntry row)
    {
        var known = row.ContainerId.Length > 0
                    && _audioContainers.TryGetValue(row.ContainerId, out _);
        row.AudioConnectable = known;
        row.AudioActive = known && _audioContainers[row.ContainerId];
    }

    /// <summary>
    ///     Starts the live Bluetooth and Wi-Fi feeds.
    ///     Both are push, not poll, because that is the difference between a picker
    ///     that feels dead and one that behaves like the Windows applet. The
    ///     blocking Bluetooth enumeration takes ~30 s before showing anything; the
    ///     watcher reports the first device in about 10 ms. Wi-Fi likewise: the
    ///     driver refreshes its scan list when it feels like it, so an interval
    ///     either wastes work or shows a network seconds late.
    /// </summary>
    private void StartFeeds()
    {
        if (_feedsStarted)
        {
            return;
        }

        _feedsStarted = true;
        _bluetoothCatalog.BeginSweep();
        StartBluetoothFeed();
        StartWifiFeed();
    }

    /// <summary>
    ///     Starts this manager's Bluetooth watch. Creating and starting the watcher returns at once
    ///     (the sweep runs on Windows' threads), and starting it here on the UI thread records the
    ///     registration before any change it posts is handled.
    /// </summary>
    private void StartBluetoothFeed()
    {
        IDisposable? registration = null;
        try
        {
            registration = WindowsRadio.StartBluetoothWatch(change =>
                Dispatcher.UIThread.Post(() => OnBluetoothChanged(registration, change)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio feed operation failed: {ex.Message}");
            BluetoothScanning = false;
            return;
        }

        _bluetoothWatch = registration;
    }

    private void StopBluetoothFeed()
    {
        var registration = _bluetoothWatch;
        _bluetoothWatch = null;
        // Revokes the watcher's handlers; a change already posted is dropped by
        // OnBluetoothChanged because this registration is no longer the current one.
        registration?.Dispose();
    }

    /// <summary>
    ///     Starts this manager's Wi-Fi watch off the UI thread, because opening a WLAN handle is a
    ///     service round trip. Its events only queue a refresh, so it needs no identity check.
    /// </summary>
    private void StartWifiFeed()
    {
        _ = Task.Run(() =>
        {
            IDisposable registration;
            try
            {
                registration = WindowsRadio.StartWifiWatch(OnWifiEvent);
            }
            catch (Exception ex)
            {
                Log.Warn($"Radio feed operation failed: {ex.Message}");
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // The feeds stopped, or restarted with a watch of their own, while this one started.
                if (_disposed || !_feedsStarted || _wifiWatch is not null)
                {
                    DisposeWifiWatch(registration);
                    return;
                }

                _wifiWatch = registration;
            });
        });
    }

    /// <summary>
    ///     Disposes a Wi-Fi watch off the UI thread: the unregister waits for a callback that is
    ///     running, and never runs inside one.
    /// </summary>
    private static void DisposeWifiWatch(IDisposable registration)
    {
        _ = Task.Run(() =>
        {
            try
            {
                registration.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Radio feed operation failed: {ex.Message}");
            }
        });
    }

    private void StopFeeds()
    {
        if (!_feedsStarted)
        {
            return;
        }

        _feedsStarted = false;
        StopBluetoothFeed();
        if (_wifiWatch is { } wifi)
        {
            _wifiWatch = null;
            DisposeWifiWatch(wifi);
        }
    }

    private void OnBluetoothChanged(IDisposable? registration, WindowsRadio.BluetoothChange change)
    {
        // A change posted by a registration this manager has since stopped or replaced is dropped:
        // a fresh sweep may already be running.
        if (_disposed || registration is null || !ReferenceEquals(registration, _bluetoothWatch))
        {
            return;
        }

        if (change.Kind == WindowsRadio.BluetoothChangeKind.Stopped)
        {
            // Windows aborted the watcher. The registration reports nothing more, so it goes; the
            // next rescan or panel opening starts a new one.
            Log.Warn("Bluetooth: Windows stopped the device watcher.");
            StopBluetoothFeed();
            BluetoothScanning = false;
            return;
        }

        var device = change.Device;
        ApplyDeviceChange(
            change.Kind,
            device.Id,
            device.Name,
            device.Paired,
            device.CanPair,
            device.Connected,
            device.Container);
    }

    private void OnWifiEvent(WindowsRadio.WifiWatchEvent change)
    {
        // A scan-list refresh means new networks are visible right now; a
        // connection change means the "connected" marker moved.
        Dispatcher.UIThread.Post(QueueRefresh);
        if (change == WindowsRadio.WifiWatchEvent.ConnectionChanged)
        {
            Log.Info("Wi-Fi: connection state changed.");
        }
    }

    /// <summary>Applies one watcher event to the device list.</summary>
    /// <param name="change">What the watcher reported.</param>
    /// <param name="id">The device id.</param>
    /// <param name="name">The display name.</param>
    /// <param name="paired">Whether it is paired.</param>
    /// <param name="canPair">Whether it can be paired.</param>
    /// <param name="connected">Whether it has a live connection.</param>
    /// <param name="container">The device container id, or empty.</param>
    private void ApplyDeviceChange(
        WindowsRadio.BluetoothChangeKind change, string id, string name, bool paired,
        bool canPair, bool connected, string container)
    {
        var logical = _bluetoothCatalog.Apply(change,
            new WindowsRadio.BluetoothDevice(id, name, paired, canPair, connected, container));
        // One pass over the current rows instead of a search per device: rows by logical id, and
        // for each endpoint the positions of the rows that list it.
        var rowsById = new Dictionary<string, BluetoothDeviceEntry>(StringComparer.Ordinal);
        var rowsByEndpoint = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < BluetoothDevices.Count; index++)
        {
            var candidate = BluetoothDevices[index];
            rowsById.TryAdd(candidate.Id, candidate);
            foreach (var endpoint in candidate.EndpointIds)
            {
                if (!rowsByEndpoint.TryGetValue(endpoint, out var positions))
                {
                    rowsByEndpoint[endpoint] = positions = [];
                }

                positions.Add(index);
            }
        }

        var retained = new HashSet<BluetoothDeviceEntry>();
        var containers = new HashSet<string>(StringComparer.Ordinal);
        var identities = new StringBuilder();
        foreach (var device in logical)
        {
            if (!rowsById.TryGetValue(device.Id, out var row))
            {
                // The row that inherits a merged or split device: one sharing an endpoint and not
                // already taken, a busy row first, then the earliest.
                var best = -1;
                foreach (var endpoint in device.EndpointIds)
                {
                    if (!rowsByEndpoint.TryGetValue(endpoint, out var positions))
                    {
                        continue;
                    }

                    foreach (var position in positions)
                    {
                        var match = BluetoothDevices[position];
                        if (!retained.Contains(match)
                            && (best < 0
                                || (match.Busy && !BluetoothDevices[best].Busy)
                                || (match.Busy == BluetoothDevices[best].Busy && position < best)))
                        {
                            best = position;
                        }
                    }
                }

                row = best < 0 ? null : BluetoothDevices[best];
            }

            if (row is null)
            {
                row = new BluetoothDeviceEntry(device.Id);
                BluetoothDevices.Add(row);
            }

            retained.Add(row);
            if (device.Container.Length > 0)
            {
                containers.Add(device.Container);
            }

            row.EndpointId = device.EndpointId;
            row.PairingEndpointId = device.PairingEndpointId;
            row.EndpointIds = device.EndpointIds;
            row.Name = device.Name;
            row.Paired = device.Paired;
            row.CanPair = device.CanPair;
            row.Connected = device.Connected;
            row.ContainerId = device.Container;
            ApplyAudioState(row);
            identities.Append(identities.Length > 0 ? "; " : "")
                .Append(
                    $"{device.Id} container={device.Container}, endpoints={string.Join(",", device.EndpointIds)}, selected={device.EndpointId}");
        }

        for (var index = BluetoothDevices.Count - 1; index >= 0; index--)
        {
            var row = BluetoothDevices[index];
            var merged = row.ContainerId.Length > 0 && containers.Contains(row.ContainerId);
            if (!retained.Contains(row) && (!row.Busy || merged))
            {
                BluetoothDevices.RemoveAt(index);
            }
        }

        // One line per change set, written only when the logical identities changed.
        Log.Change("bluetooth-identities", $"Bluetooth logical devices: {identities}.");

        if (change == WindowsRadio.BluetoothChangeKind.EnumerationCompleted)
        {
            BluetoothScanning = false;
            Log.Info($"Bluetooth discovery complete ({BluetoothDevices.Count} logical device(s)).");
        }
    }

    private static RadioPower ReadPower(WindowsRadio.RadioKind kind)
    {
        try
        {
            return WindowsRadio.GetPower(kind);
        }
        catch (Exception ex)
        {
            Log.Change($"radio-power-{kind}", $"Radio power query failed for kind {kind}: {ex.Message}");
            return RadioPower.Unknown;
        }
    }

    private void Apply(Snapshot snapshot)
    {
        WifiPower = snapshot.WifiPower;
        BluetoothPower = snapshot.BluetoothPower;
        BluetoothConnectedCount = snapshot.BluetoothConnected;
        ApplyWifiStatus(snapshot.Wifi);

        if (snapshot.Failure is { Length: > 0 } failure)
        {
            StatusText = DescribeScanFailure(snapshot.FailureStatus, failure);
            // Re-claimed AFTER the setter cleared it: this message is the one
            // that may be withdrawn when scanning recovers.
            _statusIsScanFailure = true;
        }
        else if (_statusIsScanFailure && snapshot.IncludedNetworks)
        {
            // The scan recovered, so its complaint goes. Tracked rather than
            // blanket-cleared: a connect or pairing result shown since must not
            // be wiped by an unrelated successful snapshot.
            _statusIsScanFailure = false;
            StatusText = "";
        }

        // Only when the snapshot actually carried a network list: reconciling
        // the always-empty closed-panel list would wipe the rows and zero the
        // signal that was just set.
        if (snapshot.IncludedNetworks)
        {
            ReconcileNetworks(snapshot.Networks);
        }

        if (snapshot.AudioContainers is not { } audio)
        {
            return;
        }

        _audioContainers.Clear();
        foreach (var container in audio)
        {
            // Active kept, not just the id: it is what the Connect button
            // actually toggles, and the row's broader AEP state can say
            // "connected" while the audio endpoints sit unplugged.
            _audioContainers[container.Container] = container.Active;
        }

        foreach (var row in BluetoothDevices)
        {
            ApplyAudioState(row);
        }
    }

    /// <summary>
    ///     Turns a scan failure into something actionable. The consent gate
    ///     is the case worth naming: it is not a permissions problem the user can
    ///     solve by elevating, and no amount of retrying will clear it.
    /// </summary>
    /// <param name="status">
    ///     The WLAN status of the failure, or zero when it carried none. Windows 11 24H2 answers
    ///     ERROR_ACCESS_DENIED (5) while location access is off.
    /// </param>
    /// <param name="message">The failure's message, shown as it is for every other status.</param>
    /// <returns>Location-consent guidance for status 5, otherwise a scan failure containing the supplied message.</returns>
    internal static string DescribeScanFailure(int status, string message)
    {
        return status == 5
            ? "Windows is blocking the Wi-Fi scan until location access is allowed "
              + "(Settings > Privacy & security > Location)."
            : $"Wi-Fi scan failed: {message}";
    }

    /// <summary>
    ///     Merges a fresh network list into the bound collection without
    ///     replacing surviving rows — a wholesale rebuild would move focus out from
    ///     under the gamepad cursor mid-scan.
    /// </summary>
    private void ReconcileNetworks(IReadOnlyList<WindowsRadio.WifiNetwork> fresh)
    {
        var connected = "";
        for (var i = 0; i < fresh.Count; i++)
        {
            var source = fresh[i];
            var row = FindNetwork(source.Key);
            if (row is null)
            {
                row = new WifiNetworkEntry(source.Key);
                Networks.Insert(Math.Min(i, Networks.Count), row);
            }
            else
            {
                var at = Networks.IndexOf(row);
                if (at != i && i < Networks.Count)
                {
                    Networks.Move(at, i);
                }
            }

            row.Signal = source.Signal;
            row.Security = source.Security;
            row.Saved = source.Saved;
            // Carried through rather than dropped: a network the driver has
            // already rejected must not show an enabled Connect that can only
            // fail.
            row.Connectable = source.Connectable;
            // Reported by the WLAN service, never guessed from list position:
            // the joined network is not always the strongest one visible.
            row.Connected = source.Connected;
            if (row.Connected)
            {
                connected = row.Ssid;
            }
        }

        for (var i = Networks.Count - 1; i >= fresh.Count; i--)
        {
            Networks.RemoveAt(i);
        }

        // Only when the scan positively named a joined network. The interface
        // status read in Apply is authoritative and already correct; no row
        // being marked connected (a hidden network, or a scan refresh
        // mid-flight) is not evidence of a disconnect.
        if (connected.Length > 0)
        {
            ConnectedSsid = connected;
        }
    }

    private WifiNetworkEntry? FindNetwork(WindowsRadio.WifiNetworkKey key)
    {
        return Networks.FirstOrDefault(entry => entry.Key == key);
    }

    // ---- commands ----

    /// <summary>Turns a radio on or off.</summary>
    /// <param name="bluetooth">True for the Bluetooth radio, false for Wi-Fi.</param>
    /// <param name="on">The state to switch to.</param>
    /// <returns>Completion of the serialized power request. Failures update StatusText; the queued refresh is not awaited.</returns>
    public async Task SetRadioAsync(bool bluetooth, bool on)
    {
        var kind = bluetooth ? WindowsRadio.RadioKind.Bluetooth : WindowsRadio.RadioKind.WiFi;
        var label = bluetooth ? "Bluetooth" : "Wi-Fi";
        // One power change at a time per radio. Two Task.Run delegates can
        // reach Windows in either order, so a quick Off-then-On
        // could settle with the radio OFF while the switch shows on — and the
        // two completions would overwrite each other's status besides.
        var gate = bluetooth ? _bluetoothPowerGate : _wifiPowerGate;
        await gate.WaitAsync();
        try
        {
            var result = await Task.Run(() => WindowsRadio.SetPower(kind, on));
            var failed = result.Adapters.Where(adapter => adapter.Access is null).ToArray();
            if (failed.Length > 0)
            {
                // An adapter whose write failed keeps today's command-failure text; the log names
                // each adapter and its HRESULT, so a partial change is diagnosable.
                var operation = $"turn {label} {(on ? "on" : "off")}";
                Log.Warn($"Radio command failed ({operation}): "
                         + string.Join(", ", failed.Select(adapter => $"{adapter.Name} 0x{adapter.HResult:X8}"))
                         + ".");
                StatusText = $"Could not {operation}.";
            }
            else
            {
                ApplyRadioResult(label, on, (int)result.Access);
            }
        }
        catch (Exception ex)
        {
            ReportCommandFailure($"turn {label} {(on ? "on" : "off")}", ex);
        }
        finally
        {
            gate.Release();
        }

        QueueRefresh();
    }

    /// <summary>
    ///     Turns a failed radio command into recoverable feature state.
    ///     Every enumeration path already degrades to "controls stay neutral", but a command's callers are async
    ///     void UI handlers: an escaping exception reaches the process-wide
    ///     unhandled hook and tears the game-mode session down over a button press.
    /// </summary>
    /// <param name="operation">What the user asked for, phrased for a status line.</param>
    /// <param name="ex">The failure the Windows call raised.</param>
    private void ReportCommandFailure(string operation, Exception ex)
    {
        Log.Warn($"Radio command failed ({operation}): {ex.Message}");
        StatusText = $"Could not {operation}.";
    }

    private void ApplyRadioResult(string label, bool on, int access)
    {
        if (access != 0)
        {
            // Access is refused by a privacy setting, not by anything we can fix.
            if (!_accessLogged)
            {
                _accessLogged = true;
                Log.Warn($"Radio control denied (access code {access}).");
            }

            StatusText = "Windows is not allowing apps to control the radios "
                         + "(Settings > Privacy & security > Radios).";
        }
        else
        {
            Log.Info($"Radio set {label}={on}.");
            StatusText = "";
        }
    }

    /// <summary>Joins a network, installing a profile with the password first.</summary>
    /// <param name="network">The network to join, as its row's <see cref="WifiNetworkEntry.Key" />.</param>
    /// <param name="password">The password, or null for an open or saved network.</param>
    /// <returns>
    ///     True when the network was actually joined; false leaves a
    ///     reason in <see cref="StatusText" />.
    /// </returns>
    public async Task<bool> ConnectAsync(WindowsRadio.WifiNetworkKey network, string? password)
    {
        var ssid = network.DisplayText;
        // Serialize profile mutation and completion watchers for this manager.
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
        {
            Log.Info($"Wi-Fi connect: {ssid} ignored, an attempt is already running.");
            StatusText = "Still working on the last connection attempt...";
            return false;
        }

        try
        {
            StatusText = $"Connecting to {ssid}...";
            var result = await Task.Run(() => WindowsRadio.ConnectWifi(network, password));
            switch (result.Outcome)
            {
                case WindowsRadio.WifiConnectOutcome.Joined:
                    Log.Info($"Wi-Fi connect: {ssid} connected.");
                    StatusText = "";
                    QueueRefresh();
                    return true;
                case WindowsRadio.WifiConnectOutcome.Failed:
                    var verdict = WindowsRadio.GetReasonVerdict(result.ReasonCode);
                    Log.Warn(
                        $"Wi-Fi connect: {ssid} failed (verdict {verdict}, reason {result.ReasonCode}).");
                    break;
                case WindowsRadio.WifiConnectOutcome.Pending:
                    // The attempt may still complete; the watch and the next refresh report it.
                    Log.Warn($"Wi-Fi connect: {ssid} had no verdict before the wait ended.");
                    break;
                default:
                    // Refused before anything was written.
                    Log.Warn($"Wi-Fi connect: {ssid} refused ({result.Refusal}).");
                    StatusText = DescribeConnectResult(result);
                    return false;
            }

            StatusText = DescribeConnectResult(result);
            QueueRefresh();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi connect: {ssid} threw: {ex.Message}");
            StatusText = DescribeConnectFailure(
                WindowsRadio.WifiFailureKind.Unknown, 0, ex.Message);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary>
    ///     Formats a refused, pending or failed join; definite failures use WLAN reason classification.
    /// </summary>
    /// <param name="result">WindowsDeviceControl join result.</param>
    /// <returns>A user-facing failure/pending message, or empty for Joined and unclassified outcomes.</returns>
    internal static string DescribeConnectResult(WindowsRadio.WifiConnectResult result)
    {
        return result.Outcome switch
        {
            WindowsRadio.WifiConnectOutcome.Failed => DescribeConnectFailure(
                WindowsRadio.GetReasonVerdict(result.ReasonCode), result.ReasonCode, ""),
            WindowsRadio.WifiConnectOutcome.Pending => "The Wi-Fi connection attempt did not complete.",
            WindowsRadio.WifiConnectOutcome.Refused => result.Refusal switch
            {
                WindowsRadio.WifiConnectRefusal.InvalidPassphrase =>
                    "The password must be 8-63 printable ASCII characters, or 64 hex digits. (Parameter 'passphrase')",
                WindowsRadio.WifiConnectRefusal.UnsupportedAuthentication =>
                    "This network does not advertise a supported personal-key authentication method.",
                WindowsRadio.WifiConnectRefusal.NeedsPassword =>
                    "This network needs a password and has no saved profile.",
                _ => "This network's authentication method is not supported."
            },
            _ => ""
        };
    }

    /// <summary>
    ///     Formats a classified join failure without treating connection timeouts as rejected credentials.
    /// </summary>
    /// <param name="verdict">Classified WLAN reason.</param>
    /// <param name="reasonCode">Raw WLAN reason code, or zero when none was supplied.</param>
    /// <param name="fallback">Diagnostic text used for an unknown failure without a reason code.</param>
    /// <returns>A failure message, using Windows reason text when the classification requires it.</returns>
    internal static string DescribeConnectFailure(
        WindowsRadio.WifiFailureKind verdict,
        uint reasonCode,
        string fallback)
    {
        return verdict switch
        {
            WindowsRadio.WifiFailureKind.KeyRejected =>
                "That password was not accepted. Check it and try again.",
            WindowsRadio.WifiFailureKind.SecurityMismatch => reasonCode != 0
                ? WindowsRadio.ReasonText(reasonCode)
                : "That password is not valid for this network.",
            WindowsRadio.WifiFailureKind.Unreachable =>
                "Could not reach that network. It may be out of range.",
            _ => reasonCode != 0
                ? WindowsRadio.ReasonText(reasonCode)
                : fallback.Length > 0
                    ? fallback
                    : "Could not connect."
        };
    }

    /// <summary>Leaves the current network.</summary>
    /// <returns>Completion of the disconnect request. Failures update StatusText; it does not wait for a disconnected-state observation.</returns>
    public async Task DisconnectAsync()
    {
        try
        {
            await Task.Run(WindowsRadio.DisconnectWifi);
            Log.Info("Wi-Fi disconnect: requested.");
            StatusText = "";
        }
        catch (Exception ex)
        {
            ReportCommandFailure("disconnect from this network", ex);
        }

        QueueRefresh();
    }

    /// <summary>Deletes every saved profile of a network, so it stops joining automatically.</summary>
    /// <param name="network">The network to forget, as its row's <see cref="WifiNetworkEntry.Key" />.</param>
    /// <returns>Completion of the deletion attempts. Partial failures update StatusText; a refresh is queued afterward.</returns>
    public async Task ForgetAsync(WindowsRadio.WifiNetworkKey network)
    {
        var ssid = network.DisplayText;
        try
        {
            var deletions = await Task.Run(() => WindowsRadio.ForgetWifi(network));
            var kept = deletions.Where(deletion => deletion.Status != 0).ToArray();
            if (kept.Length > 0)
            {
                Log.Warn($"Radio command failed (forget {ssid}): "
                         + string.Join(", ", kept.Select(deletion =>
                             $"{deletion.ProfileName} (Win32 {deletion.Status})"))
                         + ".");
                StatusText = $"Could not forget {ssid}.";
            }
            else
            {
                Log.Info($"Wi-Fi forget: {ssid}.");
                StatusText = "";
            }
        }
        catch (Exception ex)
        {
            ReportCommandFailure($"forget {ssid}", ex);
        }

        QueueRefresh();
    }

    /// <summary>
    ///     Connects or disconnects a paired Bluetooth audio device — the
    ///     soft action, distinct from removing the pairing. Only meaningful for
    ///     rows with <see cref="BluetoothDeviceEntry.AudioConnectable" />: other
    ///     device classes reconnect on their own initiative and Windows offers no
    ///     general reconnect operation for them.
    /// </summary>
    /// <param name="entry">The device to connect or disconnect.</param>
    /// <param name="connect">True to connect, false to disconnect.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches Windows.</param>
    /// <returns>Whether Windows accepted the request.</returns>
    public async Task<bool> SetAudioConnectionAsync(BluetoothDeviceEntry entry, bool connect,
        CancellationToken cancellationToken = default)
    {
        if (entry.Busy)
        {
            StatusText = "A Bluetooth operation is already in progress for this device.";
            return false;
        }

        if (!entry.Paired || !entry.AudioConnectable || entry.ContainerId.Length == 0)
        {
            StatusText =
                "This device reconnects when powered on or used. Windows does not expose a manual connection action for it.";
            return false;
        }

        entry.Busy = true;
        StatusText = $"{(connect ? "Connecting" : "Disconnecting")} {entry.Name}...";
        var container = entry.ContainerId;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => _connectBluetoothAudio(container, connect), cancellationToken);
            Log.Info($"Bluetooth audio {(connect ? "connect" : "disconnect")}: {entry.Name}.");
            // The accepted request is the observed state. The endpoint takes a moment to follow, so
            // the next refresh corrects the row if the device did not; nothing waits on readback.
            entry.AudioActive = connect;
            StatusText = "";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Bluetooth audio {(connect ? "connect" : "disconnect")} failed for "
                     + $"{entry.Name}: {ex.Message}");
            StatusText = connect
                ? $"Could not connect {entry.Name}. Make sure it is switched on and in range."
                : $"Could not disconnect {entry.Name}.";
            return false;
        }
        finally
        {
            // Cleared on every path: a row left busy keeps its buttons disabled
            // for as long as the panel stays open.
            entry.Busy = false;
        }
    }

    /// <summary>Removes a Bluetooth pairing.</summary>
    /// <param name="entry">The device to unpair.</param>
    /// <returns>Whether Windows confirmed removal.</returns>
    public async Task<bool> UnpairAsync(BluetoothDeviceEntry entry)
    {
        if (entry.Busy)
        {
            StatusText = "A Bluetooth operation is already in progress for this device.";
            return false;
        }

        entry.Busy = true;
        var id = entry.EndpointId;
        WindowsRadio.BluetoothUnpairResult result;
        try
        {
            result = await Task.Run(() => WindowsRadio.UnpairBluetooth(id));
        }
        catch (Exception ex)
        {
            ReportCommandFailure($"remove {entry.Name}", ex);
            return false;
        }
        finally
        {
            // Cleared on every path: a row left busy keeps its buttons disabled
            // for as long as the panel stays open.
            entry.Busy = false;
        }

        // Reflect the known outcome immediately. The background discovery would
        // confirm it eventually, but it performs a real inquiry and can take
        // half a minute — far too long for a button the user just pressed.
        var removed = result.Unpaired;
        if (removed)
        {
            _bluetoothCatalog.ConfirmPairing(id, false);
            entry.Paired = false;
        }

        Log.Info($"Bluetooth unpair: {entry.Name} -> {removed}"
                 + $"{(removed ? "" : $" (Windows status {result.NativeStatus})")}.");
        StatusText = removed ? "" : $"Could not remove {entry.Name}.";
        return removed;
    }

    /// <summary>
    ///     Starts pairing a device. Questions arrive on
    ///     <see cref="PairingRequested" /> and must be answered with
    ///     <see cref="RespondToPairing" />.
    /// </summary>
    /// <param name="entry">The device to pair.</param>
    /// <returns>Whether pairing was started or the device was already paired.</returns>
    /// <exception cref="ObjectDisposedException">The manager has been disposed.</exception>
    public bool BeginPairing(BluetoothDeviceEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.Paired)
        {
            return true;
        }

        if (_pairingInProgress || entry.Busy)
        {
            StatusText = "Another pairing is already in progress.";
            return false;
        }

        if (!entry.CanPair)
        {
            StatusText = "Put the device into pairing mode and scan again.";
            return false;
        }

        _pairingInProgress = true;
        entry.Busy = true;
        _pairingEntry = entry;
        _pairingEndpointId = entry.PairingEndpointId;
        _pairingCancelled = false;
        StatusText = $"Pairing with {entry.Name}...";
        // Discovery must stay active to keep an advertising association endpoint pair-ready.
        Log.Info($"Bluetooth pairing: started for {entry.Name}.");

        var cancellation = new CancellationTokenSource();
        Task<WindowsRadio.PairingResult> pairing;
        try
        {
            pairing = _pairBluetooth(_pairingEndpointId, OnPairingRequested, cancellation.Token);
        }
        catch (Exception ex)
        {
            FinishPairing();
            ReportCommandFailure($"pair with {entry.Name}", ex);
            return false;
        }

        _pairingCancellation = cancellation;
        _ = ObservePairingAsync(pairing, cancellation);
        return true;
    }

    /// <summary>Waits for the attempt BeginPairing started and reports how it ended.</summary>
    private async Task ObservePairingAsync(
        Task<WindowsRadio.PairingResult> pairing,
        CancellationTokenSource cancellation)
    {
        WindowsRadio.PairingResult? result = null;
        Exception? failure = null;
        try
        {
            result = await pairing;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The source has no timer or linked parent, so it is dropped rather than disposed: a cancel
        // Dispose requested may still be running its callbacks on the thread pool.
        if (ReferenceEquals(_pairingCancellation, cancellation))
        {
            _pairingCancellation = null;
        }

        OnPairingDone(result, failure);
    }

    /// <summary>Marks this row's running pairing as cancelled and declines its current or next prompt.</summary>
    /// <param name="entry">The logical Bluetooth row whose attempt should end.</param>
    /// <returns>False when another row or no row is pairing; true after recording cancellation intent.</returns>
    /// <remarks>UI thread only. Does not cancel the backend token or wait for pairing completion.</remarks>
    internal bool CancelPairing(BluetoothDeviceEntry entry)
    {
        if (!_pairingInProgress || _pairingEntry?.Id != entry.Id)
        {
            return false;
        }

        _pairingCancelled = true;
        StatusText = $"Cancelling pairing with {entry.Name}...";
        if (_pairingToken != 0)
        {
            RespondToPairing(_pairingToken, false, null);
        }

        return true;
    }

    /// <summary>Answers a pairing question raised on <see cref="PairingRequested" />.</summary>
    /// <param name="token">The token from the prompt.</param>
    /// <param name="accept">Whether the user accepted.</param>
    /// <param name="pin">The PIN typed by the user, for the provide-pin ceremony.</param>
    /// <remarks>Queues the answer on the MTA thread pool and returns immediately; native failures are logged.</remarks>
    public static void RespondToPairing(uint token, bool accept, string? pin)
    {
        Log.Info($"Bluetooth pairing: answering token {token} with "
                 + $"{(accept ? "accept" : "decline")}{(pin is { Length: > 0 } ? " and a PIN" : "")}.");
        // A pairing deferral completed on Avalonia's STA thread can wedge the
        // Device Association service; always answer from the MTA thread pool.
        _ = Task.Run(() =>
        {
            try
            {
                WindowsRadio.RespondToPairing(token, accept, pin);
            }
            catch (Exception ex)
            {
                // Unanswered, Windows' deferral sits until it times out and the
                // row stays on "Working..." — so the reason must reach the log
                // rather than an unobserved task.
                Log.Warn($"Bluetooth pairing: reply to token {token} threw: {ex.Message}");
            }
        });
    }

    private void FinishPairing()
    {
        if (_pairingEntry is not null)
        {
            _pairingEntry.Busy = false;
            _pairingEntry = null;
        }

        _pairingToken = 0;
        _pairingEndpointId = null;
        _pairingCancelled = false;
        _pairingInProgress = false;
        UpdateScanning();
    }

    private void OnPairingRequested(WindowsRadio.PairingRequest request)
    {
        if (_disposed)
        {
            RespondToPairing(request.Token, false, null);
            return;
        }

        Log.Info($"Bluetooth pairing: question received (token {request.Token}, "
                 + $"kind {request.Kind}) for {request.DeviceName}.");
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _pairingCancelled)
            {
                _pairingToken = 0;
                RespondToPairing(request.Token, false, null);
                return;
            }

            // Recorded here, on the UI thread that reads it: a cancel that runs before this post
            // sees no token, and the cancelled flag above declines the question instead.
            _pairingToken = request.Token;
            var handled = PairingRequested is not null;
            Log.Info($"Bluetooth pairing: prompting the user (token {request.Token}, "
                     + $"handler attached: {handled}).");
            PairingRequested?.Invoke(new PairingPrompt(
                request.Token, request.Kind, request.Pin, request.DeviceName));
            if (handled)
            {
                return;
            }

            Log.Warn($"Bluetooth pairing: no UI attached, declining token {request.Token}.");
            RespondToPairing(request.Token, false, null);
        });
    }

    private void OnPairingDone(WindowsRadio.PairingResult? result, Exception? failure)
    {
        if (_disposed)
        {
            return;
        }

        var outcome = result?.Outcome;
        // The library's deadline is a typed exception; the line the user sees stays today's.
        var text = failure is TimeoutException
            ? "Bluetooth pairing timed out."
            : failure?.Message
              ?? (result is { } completed ? $"Windows status {completed.RawStatus}" : string.Empty);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var entry = _pairingEntry;
            var name = entry?.Name ?? "device";
            // Same reasoning as unpair: apply the outcome we already know rather
            // than leaving the row stale until the next inquiry finishes.
            var paired = outcome is WindowsRadio.PairingOutcome.Paired
                or WindowsRadio.PairingOutcome.AlreadyPaired;
            if (entry is not null && paired)
            {
                if (_pairingEndpointId is { } endpointId)
                {
                    _bluetoothCatalog.ConfirmPairing(endpointId, true);
                }

                entry.Paired = true;
            }

            FinishPairing();
            var summary = DescribePairOutcome(outcome, name, text);
            // The raw status rides along: the grouped outcome deliberately
            // lumps rare statuses, and remote diagnosis needs the exact one.
            Log.Info($"Bluetooth pairing: finished for {name} (outcome {outcome}"
                     + $"{(text.Length > 0 ? $", {text}" : "")}). {summary}");
            StatusText = paired ? "" : summary;
            PairingFinished?.Invoke(summary);
        });
    }

    /// <summary>Shows a non-transient panel decision that did not reach Windows.</summary>
    /// <param name="message">Short actionable text for the panel status line.</param>
    internal void ReportStatus(string message)
    {
        StatusText = message;
    }

    /// <summary>The message for a finished pairing attempt.</summary>
    /// <param name="outcome">
    ///     How it ended, or <see langword="null" /> when the attempt threw
    ///     before Windows produced a result.
    /// </param>
    /// <param name="device">The device's display name.</param>
    /// <param name="message">
    ///     The exception message or raw Windows status, used only when there
    ///     is nothing better to say.
    /// </param>
    /// <returns>A line to show the user.</returns>
    internal static string DescribePairOutcome(
        WindowsRadio.PairingOutcome? outcome,
        string device,
        string message)
    {
        return outcome switch
        {
            WindowsRadio.PairingOutcome.Paired => $"{device} is paired.",
            WindowsRadio.PairingOutcome.AlreadyPaired => $"{device} was already paired.",
            WindowsRadio.PairingOutcome.Cancelled => $"Pairing with {device} was cancelled.",
            WindowsRadio.PairingOutcome.Failed =>
                $"Could not pair with {device}. Make sure it is in pairing mode.",
            // The broker runs unelevated and may be unable to inspect an
            // elevated caller; that is a different problem from a sulky device.
            WindowsRadio.PairingOutcome.AccessDenied => $"Windows denied pairing with {device}.",
            // A hung earlier ceremony inside the Device Association service —
            // it survives WSGM, so only the radio (or a reboot) can clear it.
            WindowsRadio.PairingOutcome.AlreadyInProgress =>
                $"Windows is still busy with an earlier pairing attempt for {device}. "
                + "Turn Bluetooth off and on, then try again.",
            null => message.Length > 0 ? message : $"Pairing with {device} failed.",
            _ => $"Pairing with {device} did not complete."
        };
    }

    /// <summary>Describes a pairing question for the UI to render.</summary>
    /// <param name="Token">Identifies the request when answering.</param>
    /// <param name="Kind">
    ///     Which ceremony to present.
    ///     <see cref="WindowsRadio.PairingKind.Unknown" /> is deliberately presented as confirm-only
    ///     rather than declined: an accept is what Windows most often wants, and the log line records
    ///     the raw kind so a device that really needs another ceremony is still diagnosable.
    /// </param>
    /// <param name="Pin">The PIN to show, for display-pin and confirm-pin-match.</param>
    /// <param name="DeviceName">The device being paired.</param>
    public readonly record struct PairingPrompt(
        uint Token,
        WindowsRadio.PairingKind Kind,
        string Pin,
        string DeviceName);

    private sealed record Snapshot(
        RadioPower WifiPower,
        RadioPower BluetoothPower,
        int BluetoothConnected,
        WindowsRadio.WifiStatus Wifi,
        bool IncludedNetworks,
        IReadOnlyList<WindowsRadio.WifiNetwork> Networks,
        IReadOnlyList<CoreAudio.BluetoothAudioContainer>? AudioContainers,
        string? Failure,
        int FailureStatus);
}
