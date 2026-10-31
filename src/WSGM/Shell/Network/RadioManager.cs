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
///     Owns UI-bound radio power, discovery, the Bluetooth device list and the session's radio
///     refresh, composing the Wi-Fi and pairing parts the panel and Steam reach through
///     <see cref="Wifi" /> and <see cref="Pairing" />.
/// </summary>
/// <remarks>
///     Invoke lifecycle and row-mutating actions on the Avalonia UI thread. Blocking native work runs
///     on workers and watch callbacks post updates to the dispatcher. Collections retain surviving row
///     instances to preserve focus. Dispose stops this manager's timer and watches and ends its pairing attempt.
/// </remarks>
public sealed class RadioManager : ObservableObject, IDisposable
{
    /// <summary>
    ///     How often the radio state is re-read without a live notification. The Wi-Fi and Bluetooth
    ///     feeds carry every change while the panel or Steam scans, so this only catches a driver
    ///     that stops reporting.
    /// </summary>
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(10);

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

    // One gate per radio: a Wi-Fi toggle must not wait behind a Bluetooth one.
    private readonly SemaphoreSlim _wifiPowerGate = new(1, 1);
    private bool _accessLogged;

    /// <summary>
    ///     This manager's Bluetooth watch while the feeds run. Every manager owns its own, so the
    ///     Settings preview's sheet and the session's never stop each other's discovery. Disposing it
    ///     is the stop. UI thread only.
    /// </summary>
    private IDisposable? _bluetoothWatch;

    private volatile bool _disposed;

    private bool _feedsStarted;
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
        _connectBluetoothAudio = connectBluetoothAudio;
        Wifi = new WifiController(ReportStatus, QueueRefresh);
        Wifi.PropertyChanged += OnWifiPropertyChanged;
        Pairing = new BluetoothPairing(pairBluetooth, _bluetoothCatalog, ReportStatus, UpdateScanning);
    }

    /// <summary>Gets the Wi-Fi connection state, network list and join actions.</summary>
    public WifiController Wifi { get; }

    /// <summary>Gets the session's Bluetooth pairing attempt and its questions.</summary>
    public BluetoothPairing Pairing { get; }

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
        RadioPower.On when Wifi.Connected => RadioIconState.Connected,
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
    ///     Stops the update timer, the live feeds and the pairing attempt. Idempotent; bound values
    ///     keep their last state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Wifi.Dispose();
        StopScanning();
        Pairing.Dispose();
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

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
        _timer = new DispatcherTimer { Interval = SafetyPollInterval };
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
        var wanted = !_disposed && (_panelScanning || _steamScanning || Pairing.InProgress);
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
        // A safety net only: the live feeds carry every real change while the panel or Steam
        // scans, so this is here to catch a driver that stops reporting, not to drive the UI.
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
        var wifi = WifiController.ReadStatus();

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
        Wifi.StartFeed();
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

    private void StopFeeds()
    {
        if (!_feedsStarted)
        {
            return;
        }

        _feedsStarted = false;
        StopBluetoothFeed();
        Wifi.StopFeed();
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
        Wifi.ApplyStatus(snapshot.Wifi);

        if (snapshot.Failure is { Length: > 0 } failure)
        {
            StatusText = WifiController.DescribeScanFailure(snapshot.FailureStatus, failure);
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
            Wifi.ReconcileNetworks(snapshot.Networks);
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
            StatusText = DescribeCommandFailure($"turn {label} {(on ? "on" : "off")}", ex);
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
    /// <returns>The status line to show; the failure has already been logged.</returns>
    internal static string DescribeCommandFailure(string operation, Exception ex)
    {
        Log.Warn($"Radio command failed ({operation}): {ex.Message}");
        return $"Could not {operation}.";
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
            StatusText = DescribeCommandFailure($"remove {entry.Name}", ex);
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

    /// <summary>Shows a line on the panel's status, from the manager's parts or a panel decision.</summary>
    /// <param name="message">Short actionable text for the panel status line, or empty to clear it.</param>
    internal void ReportStatus(string message)
    {
        StatusText = message;
    }

    private void OnWifiPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The pill combines the radio's power, owned here, with the join state the Wi-Fi part owns.
        if (e.PropertyName == nameof(WifiController.Connected))
        {
            Raise(nameof(WifiIconState));
        }
    }

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
