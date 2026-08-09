using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Wi-Fi and Bluetooth state and control for the game-mode UI, over the
/// native radio helper.
///
/// Windows' own radio flyouts are unreachable in game mode — there is no
/// Explorer shell to host them, and `ms-settings:` cannot activate without one —
/// so this is the only way a user on a handheld can join a network or pair a
/// controller without leaving game mode.
///
/// Every helper call blocks (WinRT round trips, WLAN handles), so nothing here
/// runs on the UI thread: a background refresh publishes results back through
/// the dispatcher. Rows are reconciled in place, because rebuilding the
/// collections would drop the control under the gamepad cursor.</summary>
public sealed class RadioManager : INotifyPropertyChanged, IDisposable
{
    /// <summary>Raised after a status property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when Windows asks a pairing question and the UI must
    /// answer with <see cref="RespondToPairing"/>. Always on the UI thread.</summary>
    public event Action<PairingPrompt>? PairingRequested;

    /// <summary>Raised when a pairing attempt finishes, with a message to show.
    /// Always on the UI thread.</summary>
    public event Action<string>? PairingFinished;

    // Radio kinds as the helper numbers them.
    private const int KindWifi = 0;
    private const int KindBluetooth = 1;

    private DispatcherTimer? _timer;
    private int _ticks;
    private int _refreshing;
    private bool _scanning;
    private bool _helperMissingLogged;
    private bool _accessLogged;

    /// <summary>Describes a pairing question for the UI to render.</summary>
    /// <param name="Token">Identifies the request when answering.</param>
    /// <param name="Kind">0 confirm-only, 1 display-pin, 2 provide-pin, 3 confirm-pin-match.</param>
    /// <param name="Pin">The PIN to show, for display-pin and confirm-pin-match.</param>
    /// <param name="DeviceName">The device being paired.</param>
    public readonly record struct PairingPrompt(uint Token, int Kind, string Pin, string DeviceName);

    /// <summary>Gets the Wi-Fi networks in range, strongest first.</summary>
    public ObservableCollection<WifiNetworkEntry> Networks { get; } = [];

    /// <summary>Gets the Bluetooth devices that are paired or visible.</summary>
    public ObservableCollection<BluetoothDeviceEntry> BluetoothDevices { get; } = [];

    private RadioPower _wifiPower = RadioPower.Unknown;
    /// <summary>Gets the Wi-Fi radio's power state.</summary>
    public RadioPower WifiPower
    {
        get => _wifiPower;
        private set
        {
            if (_wifiPower != value)
            {
                _wifiPower = value;
                Raise(nameof(WifiPower));
                Raise(nameof(WifiOn));
                Raise(nameof(WifiStateText));
                Raise(nameof(WifiIconState));
            }
        }
    }

    private RadioPower _bluetoothPower = RadioPower.Unknown;
    /// <summary>Gets the Bluetooth radio's power state.</summary>
    public RadioPower BluetoothPower
    {
        get => _bluetoothPower;
        private set
        {
            if (_bluetoothPower != value)
            {
                _bluetoothPower = value;
                Raise(nameof(BluetoothPower));
                Raise(nameof(BluetoothOn));
                Raise(nameof(BluetoothStateText));
                Raise(nameof(BluetoothIconState));
            }
        }
    }

    /// <summary>Gets whether the Wi-Fi radio is on.</summary>
    public bool WifiOn => WifiPower == RadioPower.On;

    /// <summary>Gets whether the Bluetooth radio is on.</summary>
    public bool BluetoothOn => BluetoothPower == RadioPower.On;

    /// <summary>Gets what the taskbar's Wi-Fi tile should show. Off and merely
    /// disconnected are different problems and must not look the same.</summary>
    public Controls.RadioIconState WifiIconState => WifiPower switch
    {
        RadioPower.On when WifiConnected => Controls.RadioIconState.Connected,
        RadioPower.On => Controls.RadioIconState.Disconnected,
        _ => Controls.RadioIconState.Off,
    };

    /// <summary>Gets what the taskbar's Bluetooth tile should show. Accent only
    /// when a device is actually connected — a lone powered radio is
    /// "disconnected", the same distinction the Wi-Fi tile draws.</summary>
    public Controls.RadioIconState BluetoothIconState => BluetoothPower switch
    {
        RadioPower.On when BluetoothConnectedCount > 0 => Controls.RadioIconState.Connected,
        RadioPower.On => Controls.RadioIconState.Disconnected,
        _ => Controls.RadioIconState.Off,
    };

    private int _bluetoothConnectedCount;
    /// <summary>Gets how many Bluetooth devices have a live connection. Read
    /// from PnP state every status tick, so the tile is correct whether or not
    /// the panel has ever been opened.</summary>
    public int BluetoothConnectedCount
    {
        get => _bluetoothConnectedCount;
        private set
        {
            if (_bluetoothConnectedCount != value)
            {
                _bluetoothConnectedCount = value;
                Raise(nameof(BluetoothConnectedCount));
                Raise(nameof(BluetoothIconState));
            }
        }
    }

    private bool _wifiConnected;
    /// <summary>Gets whether Wi-Fi is joined to a network — the only state that
    /// tints the taskbar's Wi-Fi tile with the accent color.</summary>
    public bool WifiConnected
    {
        get => _wifiConnected;
        private set
        {
            if (_wifiConnected != value)
            {
                _wifiConnected = value;
                Raise(nameof(WifiConnected));
                Raise(nameof(WifiIconState));
            }
        }
    }

    private int _wifiSignal;
    /// <summary>Gets the joined network's signal quality, 0-100. Drives the bars
    /// on the taskbar tile.</summary>
    public int WifiSignal
    {
        get => _wifiSignal;
        private set
        {
            if (_wifiSignal != value)
            {
                _wifiSignal = value;
                Raise(nameof(WifiSignal));
            }
        }
    }

    private string _connectedSsid = "";
    /// <summary>Gets the joined network's name, or an empty string.</summary>
    public string ConnectedSsid
    {
        get => _connectedSsid;
        private set => Set(ref _connectedSsid, value, nameof(ConnectedSsid));
    }

    private string _wifiStateText = "State unavailable";
    /// <summary>Gets the Wi-Fi state line for the taskbar tile's flyout.</summary>
    public string WifiStateText
    {
        get => _wifiStateText;
        private set => Set(ref _wifiStateText, value, nameof(WifiStateText));
    }

    private string _bluetoothStateText = "State unavailable";
    /// <summary>Gets the Bluetooth state line for the taskbar tile's flyout.</summary>
    public string BluetoothStateText
    {
        get => _bluetoothStateText;
        private set => Set(ref _bluetoothStateText, value, nameof(BluetoothStateText));
    }

    private string _statusText = "";
    /// <summary>Gets the last thing that happened, for the panel's status line.
    /// Empty when there is nothing to report.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText != value)
            {
                _statusText = value;
                Raise(nameof(StatusText));
                Raise(nameof(HasStatus));
            }
        }
    }

    /// <summary>Gets whether a status line should be shown.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Performs a first refresh and starts the update timer.
    /// UI-thread callers only. Idempotent.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }
        QueueRefresh();
        // Parameterless ctor + explicit Start: the 3-arg ctor auto-starts.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stops the update timer. Idempotent; bound values keep their last
    /// state.</summary>
    public void Dispose()
    {
        StopScanning();
        if (_timer is null)
        {
            return;
        }
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>Begins actively scanning for networks and devices. Called when
    /// the radio panel opens: an idle taskbar must not pay for scans nobody is
    /// looking at, which on a handheld is battery.</summary>
    public void StartScanning()
    {
        if (_scanning)
        {
            return;
        }
        _scanning = true;
        Log.Info("Radio panel: scanning started.");
        // Publish the cached scan list immediately — it is already there and
        // costs milliseconds — then ask for a fresh scan and let the live feeds
        // fill in the rest. Waiting for the scan before showing anything is what
        // made the list take ten seconds to appear.
        QueueRefresh();
        StartFeeds();
        Rescan();
    }

    /// <summary>Stops actively scanning. Idempotent.</summary>
    public void StopScanning()
    {
        if (!_scanning)
        {
            return;
        }
        _scanning = false;
        StopFeeds();
        BluetoothScanning = false;
        Log.Info("Radio panel: scanning stopped.");
    }

    /// <summary>Asks for a fresh sweep of both radios.
    ///
    /// Bound to the panel's refresh button: without it the only way to look for
    /// a network or a device that appeared after opening was to close and reopen
    /// the panel.</summary>
    public void Rescan()
    {
        Log.Info("Radio panel: rescan requested.");
        StatusText = "";
        if (BluetoothPower == RadioPower.On)
        {
            BluetoothScanning = true;
            // A fresh sweep starts a fresh census; stale rows are dropped when
            // it completes.
            _seenThisSweep.Clear();
            // Restarting the watcher re-runs the initial enumeration, which is
            // what picks up a device that has only just been put into pairing
            // mode. Existing rows survive because they are matched by id.
            StopAndRestartBluetoothWatch();
        }
        _ = Task.Run(() =>
        {
            if (NativeRadio.RequestWifiScan() != NativeRadio.Ok)
            {
                Log.Warn($"Wi-Fi scan request failed: {NativeRadio.LastError()}");
            }
        });
        QueueRefresh();
    }

    private void StopAndRestartBluetoothWatch()
    {
        if (!_watchHandle.IsAllocated)
        {
            return;
        }
        var context = GCHandle.ToIntPtr(_watchHandle);
        var callback = BluetoothCallback;
        QueueFeedWork(() =>
        {
            NativeRadio.StopBluetoothWatch();
            if (NativeRadio.StartBluetoothWatch(callback, context) != NativeRadio.Ok)
            {
                var error = NativeRadio.LastError();
                Dispatcher.UIThread.Post(() =>
                {
                    Log.Warn($"Bluetooth watch could not restart: {error}");
                    BluetoothScanning = false;
                });
            }
        });
    }

    /// <summary>The watcher entry points, as plain pointers so the background
    /// feed work can capture them without an unsafe closure.</summary>
    private static unsafe nint BluetoothCallback =>
        (nint)(delegate* unmanaged[Stdcall]<nint, int, nint, nint, int, int, int, nint, void>)
            &OnBluetoothChanged;

    private static unsafe nint WifiCallback =>
        (nint)(delegate* unmanaged[Stdcall]<nint, int, void>)&OnWifiEvent;

    /// <summary>Serializes the native feed operations onto background threads.
    ///
    /// Two reasons, both load-bearing. They BLOCK: every one of them crosses to
    /// the helper's single MTA worker, and starting a watcher while a radio
    /// enumeration is still running there froze the panel on open. And they
    /// must not interleave — a stop racing a start would leave the watcher in
    /// whichever state finished last. UI-thread callers only, so the field
    /// needs no lock.
    ///
    /// STATIC on purpose: the native watchers are process-wide singletons, but
    /// managers are not — closing and reopening the taskbar builds a new one
    /// while the old is still tearing down. With a queue each, the old
    /// manager's stop could land after the new manager's start and silently
    /// leave the reopened panel with no discovery at all.</summary>
    private static Task _feedWork = Task.CompletedTask;

    private void QueueFeedWork(Action work)
    {
        _feedWork = _feedWork.ContinueWith(
            _ =>
            {
                try
                {
                    work();
                }
                catch (DllNotFoundException ex)
                {
                    WarnHelperMissing(ex.Message);
                }
                catch (EntryPointNotFoundException ex)
                {
                    WarnHelperMissing(ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Radio feed operation failed: {ex.Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private bool _bluetoothScanning;
    /// <summary>Gets whether a Bluetooth sweep is still running, so the panel can
    /// show that more devices may still appear.</summary>
    public bool BluetoothScanning
    {
        get => _bluetoothScanning;
        private set => Set(ref _bluetoothScanning, value, nameof(BluetoothScanning));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        // A safety net only: the live feeds carry every real change, so this is
        // here to catch a driver that stops reporting, not to drive the UI.
        QueueRefresh();
    }

    /// <summary>Refreshes state off the UI thread, at most one at a time. A slow
    /// helper call must not queue up behind itself every tick.</summary>
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
            catch (DllNotFoundException ex)
            {
                WarnHelperMissing(ex.Message);
            }
            catch (EntryPointNotFoundException ex)
            {
                WarnHelperMissing(ex.Message);
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

    private void WarnHelperMissing(string message)
    {
        if (_helperMissingLogged)
        {
            return;
        }
        _helperMissingLogged = true;
        Log.Warn($"Radio helper unavailable, radio controls stay neutral: {message}");
    }

    private sealed record Snapshot(
        RadioPower WifiPower,
        RadioPower BluetoothPower,
        int BluetoothConnected,
        int WifiState,
        int WifiSignal,
        string WifiSsid,
        bool IncludedNetworks,
        IReadOnlyList<NativeRadio.WifiNetwork> Networks,
        IReadOnlyList<NativeRadio.BluetoothAudioContainer>? AudioContainers,
        string? Failure);

    private static Snapshot ReadSnapshot(bool includeNetworks)
    {
        var wifiPower = ReadPower(KindWifi);
        var bluetoothPower = ReadPower(KindBluetooth);

        // Answered from PnP state, no inquiry — cheap enough for every tick,
        // and the only way the tile can distinguish "on" from "connected"
        // without the panel's watcher running.
        var bluetoothConnected = 0;
        if (bluetoothPower == RadioPower.On
            && NativeRadio.ConnectedBluetoothCount(out var connectedCount) == NativeRadio.Ok)
        {
            bluetoothConnected = (int)connectedCount;
        }

        // State, signal and SSID together, every tick: reading the signal only
        // while the panel was open left the taskbar tile with no bars until the
        // panel had been opened once.
        var wifiState = 3;
        var wifiSignal = 0;
        var wifiSsid = "";
        if (NativeRadio.WifiStatus(out var rawState, out var signal, out var ssid)
            == NativeRadio.Ok)
        {
            wifiState = rawState;
            wifiSignal = signal;
            wifiSsid = ssid;
        }

        IReadOnlyList<NativeRadio.WifiNetwork> networks = [];
        string? failure = null;

        if (includeNetworks && wifiPower == RadioPower.On)
        {
            if (NativeRadio.ListWifiNetworks(out var items, out var count) == NativeRadio.Ok)
            {
                networks = ReadArray(
                    items, count, NativeRadio.WifiRecordSize, NativeRadio.ReadWifiNetwork);
                NativeRadio.FreeWifiNetworks(items, count);
            }
            else
            {
                failure = NativeRadio.LastError();
            }
        }

        // Only while the panel is open: the audio-endpoint set decides which
        // Bluetooth rows get a Connect action, and only the panel shows rows.
        // Local PnP enumeration, no radio traffic.
        IReadOnlyList<NativeRadio.BluetoothAudioContainer>? audio = null;
        if (includeNetworks
            && NativeRadio.ListBluetoothAudio(out var audioItems, out var audioCount)
                == NativeRadio.Ok)
        {
            audio = ReadArray(
                audioItems,
                audioCount,
                NativeRadio.BluetoothAudioRecordSize,
                NativeRadio.ReadBluetoothAudio);
            NativeRadio.FreeBluetoothAudio(audioItems, audioCount);
        }

        return new Snapshot(
            wifiPower,
            bluetoothPower,
            bluetoothConnected,
            wifiState,
            wifiSignal,
            wifiSsid,
            includeNetworks && wifiPower == RadioPower.On,
            networks,
            audio,
            failure);
    }

    // Watch callbacks must be static under NativeAOT, so the manager travels
    // through the context cookie rather than a closure.
    private GCHandle _watchHandle;

    /// <summary>Ids reported during the current discovery sweep. Anything absent
    /// when the sweep completes is no longer there.</summary>
    private readonly HashSet<string> _seenThisSweep = new(StringComparer.Ordinal);

    /// <summary>Containers with Bluetooth audio endpoints, mapped to whether
    /// those endpoints are live. The devices whose rows get a
    /// Connect/Disconnect action, and which way round it reads. Refreshed with
    /// each panel-open snapshot.</summary>
    private readonly Dictionary<string, bool> _audioContainers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies the audio-endpoint facts to one row.</summary>
    private void ApplyAudioState(BluetoothDeviceEntry row)
    {
        var known = row.ContainerId.Length > 0
            && _audioContainers.TryGetValue(row.ContainerId, out var active);
        row.AudioConnectable = known;
        row.AudioActive = known && _audioContainers[row.ContainerId];
    }

    /// <summary>Starts the live Bluetooth and Wi-Fi feeds.
    ///
    /// Both are push, not poll, because that is the difference between a picker
    /// that feels dead and one that behaves like the Windows applet. The
    /// blocking Bluetooth enumeration takes ~30 s before showing anything; the
    /// watcher reports the first device in about 10 ms. Wi-Fi likewise: the
    /// driver refreshes its scan list when it feels like it, so an interval
    /// either wastes work or shows a network seconds late.</summary>
    private void StartFeeds()
    {
        if (_watchHandle.IsAllocated)
        {
            return;
        }
        // Allocated here, on the UI thread, so a stop queued straight after an
        // open still sees live feeds and tears them down in order.
        _watchHandle = GCHandle.Alloc(this);
        var context = GCHandle.ToIntPtr(_watchHandle);
        var bluetooth = BluetoothCallback;
        var wifi = WifiCallback;
        QueueFeedWork(() =>
        {
            if (NativeRadio.StartBluetoothWatch(bluetooth, context) != NativeRadio.Ok)
            {
                Log.Warn($"Bluetooth watch could not start: {NativeRadio.LastError()}");
            }
            if (NativeRadio.StartWifiWatch(wifi, context) != NativeRadio.Ok)
            {
                Log.Warn($"Wi-Fi watch could not start: {NativeRadio.LastError()}");
            }
        });
    }

    private void StopFeeds()
    {
        if (!_watchHandle.IsAllocated)
        {
            return;
        }
        // Cleared before the work is queued: to every UI-thread caller the
        // feeds are already gone, while the teardown itself runs in order
        // behind whatever start is still in flight.
        var handle = _watchHandle;
        _watchHandle = default;
        QueueFeedWork(() =>
        {
            NativeRadio.StopBluetoothWatch();
            NativeRadio.StopWifiWatch();
            // Freed only after both feeds are stopped, or a late callback would
            // resolve a dead handle.
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnBluetoothChanged(
        nint context, int change, nint id, nint name, int paired, int canPair, int connected,
        nint container)
    {
        // Arrives on a WinRT worker. Copy the strings before returning.
        var deviceId = Marshal.PtrToStringUni(id) ?? "";
        var deviceName = Marshal.PtrToStringUni(name) ?? "";
        var containerId = Marshal.PtrToStringUni(container) ?? "";
        var manager = FromContext(context);
        if (manager is null)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
            manager.ApplyDeviceChange(
                change, deviceId, deviceName, paired != 0, canPair != 0, connected != 0,
                containerId));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnWifiEvent(nint context, int code)
    {
        var manager = FromContext(context);
        if (manager is null)
        {
            return;
        }
        // A scan-list refresh means new networks are visible right now; a
        // connection change means the "connected" marker moved.
        Dispatcher.UIThread.Post(manager.QueueRefresh);
        if (code == 1)
        {
            Log.Info("Wi-Fi: connection state changed.");
        }
    }

    /// <summary>Applies one watcher event to the device list.</summary>
    /// <param name="change">0 added, 1 updated, 2 removed, 3 enumeration complete.</param>
    /// <param name="id">The device id.</param>
    /// <param name="name">The display name.</param>
    /// <param name="paired">Whether it is paired.</param>
    /// <param name="canPair">Whether it can be paired.</param>
    /// <param name="connected">Whether it has a live connection.</param>
    /// <param name="container">The device container id, or empty.</param>
    private void ApplyDeviceChange(
        int change, string id, string name, bool paired, bool canPair, bool connected,
        string container)
    {
        if (change == 3)
        {
            BluetoothScanning = false;
            // Anything not seen during this sweep is gone. Windows keeps its
            // association-endpoint records long after a device stops
            // advertising, and the watcher does not always report a Removed for
            // them, so an unpaired device that has been switched off would
            // otherwise sit in the list forever. Paired devices stay: they are
            // legitimately known whether or not they are in range.
            var stale = 0;
            for (var i = BluetoothDevices.Count - 1; i >= 0; i--)
            {
                var candidate = BluetoothDevices[i];
                if (!candidate.Paired && !candidate.Busy
                    && !_seenThisSweep.Contains(candidate.Id))
                {
                    BluetoothDevices.RemoveAt(i);
                    stale++;
                }
            }
            Log.Info($"Bluetooth discovery complete ({BluetoothDevices.Count} device(s), "
                + $"{stale} stale dropped).");
            return;
        }
        if (id.Length == 0)
        {
            return;
        }
        var row = FindDevice(id);
        if (change == 2)
        {
            // A row mid-operation is never removed: a device dropping out of
            // range must not cancel the pairing the user just started. Nor is a
            // PAIRED one — it is legitimately known whether or not it is in
            // range, and dropping it would take its Paired status and Remove
            // button with it. Same rule the sweep cleanup applies.
            if (row is not null && !row.Busy && !row.Paired)
            {
                BluetoothDevices.Remove(row);
            }
            return;
        }
        _seenThisSweep.Add(id);
        if (row is null)
        {
            row = new BluetoothDeviceEntry(id);
            BluetoothDevices.Add(row);
        }
        row.Name = name;
        row.Paired = paired;
        row.CanPair = canPair;
        row.Connected = connected;
        row.ContainerId = container;
        ApplyAudioState(row);
        BluetoothStateText = DescribeBluetooth(BluetoothPower, BluetoothDevices.Count);
    }

    private static RadioPower ReadPower(int kind) =>
        NativeRadio.GetRadioPower(kind, out var state) == NativeRadio.Ok
            ? (RadioPower)state
            : RadioPower.Unknown;

    /// <summary>Copies a helper-owned array into managed memory. The caller frees
    /// the native allocation immediately afterwards, so nothing may keep a
    /// pointer into it.</summary>
    /// <param name="items">The first record, or zero.</param>
    /// <param name="count">How many records follow.</param>
    /// <param name="stride">The size of one record.</param>
    /// <param name="read">Decodes one record.</param>
    private static List<T> ReadArray<T>(nint items, uint count, int stride, Func<nint, T> read)
    {
        var result = new List<T>((int)count);
        if (items == 0)
        {
            return result;
        }
        for (var i = 0; i < count; i++)
        {
            result.Add(read(items + (i * stride)));
        }
        return result;
    }

    private void Apply(Snapshot snapshot)
    {
        WifiPower = snapshot.WifiPower;
        BluetoothPower = snapshot.BluetoothPower;
        BluetoothConnectedCount = snapshot.BluetoothConnected;
        WifiConnected = snapshot.WifiState == 0;
        // Straight from the interface, so the tile has bars whether or not the
        // panel has ever been opened.
        WifiSignal = snapshot.WifiSignal;
        ConnectedSsid = snapshot.WifiSsid;
        WifiStateText = DescribeWifi(snapshot.WifiPower, snapshot.WifiState);
        BluetoothStateText = DescribeBluetooth(snapshot.BluetoothPower, BluetoothDevices.Count);

        if (snapshot.Failure is { Length: > 0 } failure)
        {
            StatusText = DescribeScanFailure(failure);
        }

        // Only when the snapshot actually carried a network list. Reconciling
        // the always-empty closed-panel list wiped the rows AND zeroed the
        // signal that was just set — which is why the tile only showed bars
        // after the panel had been opened once.
        if (snapshot.IncludedNetworks)
        {
            ReconcileNetworks(snapshot.Networks);
        }

        if (snapshot.AudioContainers is { } audio)
        {
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
    }

    /// <summary>Turns a scan failure into something actionable. The consent gate
    /// is the case worth naming: it is not a permissions problem the user can
    /// solve by elevating, and no amount of retrying will clear it.</summary>
    internal static string DescribeScanFailure(string message) =>
        message.Contains("Win32 5", StringComparison.Ordinal)
            ? "Windows is blocking the Wi-Fi scan until location access is allowed "
              + "(Settings > Privacy & security > Location)."
            : $"Wi-Fi scan failed: {message}";

    /// <summary>The state line for the Wi-Fi tile's flyout.</summary>
    internal static string DescribeWifi(RadioPower power, int interfaceState) => power switch
    {
        RadioPower.Off => "Off",
        RadioPower.Disabled => "Blocked by Windows",
        RadioPower.Absent => "No Wi-Fi adapter",
        RadioPower.Unknown => "State unavailable",
        _ => interfaceState switch
        {
            0 => "Connected",
            1 => "Connecting...",
            2 => "Not connected",
            _ => "On",
        },
    };

    /// <summary>The state line for the Bluetooth tile's flyout.</summary>
    internal static string DescribeBluetooth(RadioPower power, int deviceCount) => power switch
    {
        RadioPower.Off => "Off",
        RadioPower.Disabled => "Blocked by Windows",
        RadioPower.Absent => "No Bluetooth adapter",
        RadioPower.Unknown => "State unavailable",
        _ => deviceCount > 0 ? $"On, {deviceCount} device(s)" : "On",
    };

    /// <summary>Merges a fresh network list into the bound collection without
    /// replacing surviving rows — a wholesale rebuild would move focus out from
    /// under the gamepad cursor mid-scan.</summary>
    private void ReconcileNetworks(IReadOnlyList<NativeRadio.WifiNetwork> fresh)
    {
        var connected = "";
        for (var i = 0; i < fresh.Count; i++)
        {
            var source = fresh[i];
            var row = FindNetwork(source.Ssid);
            if (row is null)
            {
                row = new WifiNetworkEntry(source.Ssid);
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
            row.Security = (WifiSecurity)source.Security;
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
        // status read in Apply is authoritative and already correct; clearing
        // it here because no ROW happened to be marked connected (a hidden
        // network, or a scan refresh mid-flight) made the taskbar lose its
        // network name and signal bars the moment the panel opened.
        if (connected.Length > 0)
        {
            ConnectedSsid = connected;
        }
    }

    private WifiNetworkEntry? FindNetwork(string ssid)
    {
        foreach (var entry in Networks)
        {
            if (string.Equals(entry.Ssid, ssid, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>Merges a fresh device list, same in-place discipline as the
    /// network list. Rows that are mid-operation are never removed, or a device
    /// dropping out of range would cancel the pairing the user just started.</summary>
    private void ReconcileDevices(IReadOnlyList<NativeRadio.BluetoothDevice> fresh)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in fresh)
        {
            seen.Add(source.Id);
            var row = FindDevice(source.Id);
            if (row is null)
            {
                row = new BluetoothDeviceEntry(source.Id);
                BluetoothDevices.Add(row);
            }
            row.Name = source.Name;
            row.Paired = source.Paired;
            row.CanPair = source.CanPair;
            row.Connected = source.Connected;
            row.ContainerId = source.Container;
            ApplyAudioState(row);
        }
        for (var i = BluetoothDevices.Count - 1; i >= 0; i--)
        {
            var row = BluetoothDevices[i];
            if (!row.Busy && !seen.Contains(row.Id))
            {
                BluetoothDevices.RemoveAt(i);
            }
        }
    }

    private BluetoothDeviceEntry? FindDevice(string id)
    {
        foreach (var entry in BluetoothDevices)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    // ---- commands ----

    /// <summary>Turns a radio on or off.</summary>
    /// <param name="bluetooth">True for the Bluetooth radio, false for Wi-Fi.</param>
    /// <param name="on">The state to switch to.</param>
    public async Task SetRadioAsync(bool bluetooth, bool on)
    {
        var kind = bluetooth ? KindBluetooth : KindWifi;
        var label = bluetooth ? "Bluetooth" : "Wi-Fi";
        var result = await Task.Run(() =>
        {
            var status = NativeRadio.SetRadioPower(kind, on ? 1 : 0, out var access);
            return (status, access, error: NativeRadio.LastError());
        });

        if (result.status != NativeRadio.Ok)
        {
            Log.Warn($"Radio set {label}={on} failed: {result.error}");
            StatusText = $"Could not turn {label} {(on ? "on" : "off")}.";
        }
        else if (result.access != 0)
        {
            // Access is refused by a privacy setting, not by anything we can fix.
            if (!_accessLogged)
            {
                _accessLogged = true;
                Log.Warn($"Radio control denied (access code {result.access}).");
            }
            StatusText = "Windows is not allowing apps to control the radios "
                + "(Settings > Privacy & security > Radios).";
        }
        else
        {
            Log.Info($"Radio set {label}={on}.");
            StatusText = "";
        }
        QueueRefresh();
    }

    /// <summary>Joins a network, installing a profile with the password first.</summary>
    /// <param name="ssid">The network to join.</param>
    /// <param name="password">The password, or null for an open or saved network.</param>
    /// <returns>True when the network was actually joined; false leaves a
    /// reason in <see cref="StatusText"/>.</returns>
    public async Task<bool> ConnectAsync(string ssid, string? password)
    {
        // One attempt at a time. The helper now waits out the real verdict, so
        // a second Connect would run a concurrent attempt whose scoped watcher
        // sees the same process-wide WLAN events: the two would consume each
        // other's outcomes, report the wrong result, and roll back a profile
        // over a cancellation the user never asked for.
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
        {
            Log.Info($"Wi-Fi connect: {ssid} ignored, an attempt is already running.");
            StatusText = "Still working on the last connection attempt...";
            return false;
        }
        try
        {
            StatusText = $"Connecting to {ssid}...";
            var result = await Task.Run(() =>
            {
                var status = NativeRadio.ConnectWifi(ssid, password, out var reason);
                return (status, reason, error: NativeRadio.LastError());
            });

            if (result.status == NativeRadio.Ok)
            {
                Log.Info($"Wi-Fi connect: {ssid} connected.");
                StatusText = "";
                QueueRefresh();
                return true;
            }

            var verdict = result.reason != 0
                ? await Task.Run(() => NativeRadio.GetReasonVerdict(result.reason))
                : 4;
            StatusText = DescribeConnectFailure(verdict, result.reason, result.error);
            Log.Warn(
                $"Wi-Fi connect: {ssid} failed (verdict {verdict}, reason {result.reason}): {result.error}");
            QueueRefresh();
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary>Non-zero while a connection attempt is in flight.</summary>
    private int _connecting;

    /// <summary>The message for a failed join.
    ///
    /// Only a rejected key re-prompts for a password. Blaming the user's typing
    /// for an association timeout is worse than saying the network could not be
    /// reached, because they will retype a password that was already correct.</summary>
    internal static string DescribeConnectFailure(int verdict, uint reasonCode, string fallback) =>
        verdict switch
        {
            1 => "That password was not accepted. Check it and try again.",
            2 => reasonCode != 0
                ? NativeRadio.ReasonText(reasonCode)
                : "That password is not valid for this network.",
            3 => "Could not reach that network. It may be out of range.",
            _ => reasonCode != 0
                ? NativeRadio.ReasonText(reasonCode)
                : (fallback.Length > 0 ? fallback : "Could not connect."),
        };

    /// <summary>Leaves the current network.</summary>
    public async Task DisconnectAsync()
    {
        var result = await Task.Run(() =>
        {
            var status = NativeRadio.DisconnectWifi();
            return (status, error: NativeRadio.LastError());
        });
        if (result.status == NativeRadio.Ok)
        {
            Log.Info("Wi-Fi disconnect: requested.");
            StatusText = "";
        }
        else
        {
            // The row stays connected on the next refresh, so silence here
            // reads as the button having done nothing at all.
            Log.Warn($"Wi-Fi disconnect failed: {result.error}");
            StatusText = "Could not disconnect from this network.";
        }
        QueueRefresh();
    }

    /// <summary>Deletes a saved network, so it stops joining automatically.</summary>
    /// <param name="ssid">The network to forget.</param>
    public async Task ForgetAsync(string ssid)
    {
        var result = await Task.Run(() =>
        {
            var status = NativeRadio.ForgetWifi(ssid);
            return (status, error: NativeRadio.LastError());
        });
        if (result.status == NativeRadio.Ok)
        {
            Log.Info($"Wi-Fi forget: {ssid}.");
            StatusText = "";
        }
        else
        {
            // Reported, not assumed: a profile that survived a Forget keeps
            // auto-joining, and silence here looks exactly like success.
            Log.Warn($"Wi-Fi forget: {ssid} failed: {result.error}");
            StatusText = $"Could not forget {ssid}.";
        }
        QueueRefresh();
    }

    /// <summary>Connects or disconnects a paired Bluetooth audio device — the
    /// soft action, distinct from removing the pairing. Only meaningful for
    /// rows with <see cref="BluetoothDeviceEntry.AudioConnectable"/>: other
    /// device classes reconnect on their own initiative and Windows offers no
    /// host-side connect for them.</summary>
    /// <param name="entry">The device to connect or disconnect.</param>
    /// <param name="connect">True to connect, false to disconnect.</param>
    public async Task SetAudioConnectionAsync(BluetoothDeviceEntry entry, bool connect)
    {
        if (entry.ContainerId.Length == 0)
        {
            return;
        }
        entry.Busy = true;
        StatusText = $"{(connect ? "Connecting" : "Disconnecting")} {entry.Name}...";
        var container = entry.ContainerId;
        var result = await Task.Run(() =>
        {
            var status = NativeRadio.SetBluetoothAudio(container, connect ? 1 : 0);
            return (status, error: NativeRadio.LastError());
        });
        entry.Busy = false;
        if (result.status == NativeRadio.Ok)
        {
            Log.Info($"Bluetooth audio {(connect ? "connect" : "disconnect")}: {entry.Name}.");
            // Optimistic on the AUDIO state specifically — that is what this
            // one-shot moved. The next snapshot confirms it from the endpoints.
            entry.AudioActive = connect;
            StatusText = "";
        }
        else
        {
            Log.Warn($"Bluetooth audio {(connect ? "connect" : "disconnect")} failed for "
                + $"{entry.Name}: {result.error}");
            StatusText = connect
                ? $"Could not connect {entry.Name}. Make sure it is switched on and in range."
                : $"Could not disconnect {entry.Name}.";
        }
        QueueRefresh();
    }

    /// <summary>Removes a Bluetooth pairing.</summary>
    /// <param name="entry">The device to unpair.</param>
    public async Task UnpairAsync(BluetoothDeviceEntry entry)
    {
        entry.Busy = true;
        var id = entry.Id;
        var removed = await Task.Run(() =>
            NativeRadio.UnpairBluetooth(id, out var ok) == NativeRadio.Ok && ok != 0);
        entry.Busy = false;
        // Reflect the known outcome immediately. The background discovery would
        // confirm it eventually, but it performs a real inquiry and can take
        // half a minute — far too long for a button the user just pressed.
        if (removed)
        {
            entry.Paired = false;
        }
        Log.Info($"Bluetooth unpair: {entry.Name} -> {removed}.");
        StatusText = removed ? "" : $"Could not remove {entry.Name}.";
    }

    // Pairing callbacks are static because NativeAOT requires it, so the manager
    // instance travels through the context cookie rather than a closure.
    private GCHandle _pairingHandle;
    private BluetoothDeviceEntry? _pairingEntry;

    /// <summary>Starts pairing a device. Questions arrive on
    /// <see cref="PairingRequested"/> and must be answered with
    /// <see cref="RespondToPairing"/>.</summary>
    /// <param name="entry">The device to pair.</param>
    public unsafe void BeginPairing(BluetoothDeviceEntry entry)
    {
        if (_pairingHandle.IsAllocated)
        {
            StatusText = "Another pairing is already in progress.";
            return;
        }
        entry.Busy = true;
        _pairingEntry = entry;
        // The handle keeps this manager reachable while native code holds the
        // cookie; it is freed in OnPairingDone, which always runs.
        _pairingHandle = GCHandle.Alloc(this);
        StatusText = $"Pairing with {entry.Name}...";
        // Discovery keeps running through the whole ceremony ON PURPOSE, the
        // way the Windows applet does it: PairAsync needs the association
        // endpoint pair-ready, which for an advertising device is exactly what
        // the live scan maintains. Stopping the watcher before PairAsync made
        // every attempt fail instantly (device-observed 2026-08-09).
        Log.Info($"Bluetooth pairing: started for {entry.Name}.");

        var id = entry.Id;
        var context = GCHandle.ToIntPtr(_pairingHandle);
        var status = NativeRadio.PairBluetooth(
            id,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, int, nint, nint, void>)&OnPairingRequested,
            (nint)(delegate* unmanaged[Stdcall]<nint, int, nint, void>)&OnPairingDone,
            context);
        if (status != NativeRadio.Ok)
        {
            var error = NativeRadio.LastError();
            Log.Warn($"Bluetooth pairing: could not start for {entry.Name}: {error}");
            StatusText = $"Could not start pairing with {entry.Name}.";
            FinishPairing();
        }
    }

    /// <summary>Answers a pairing question raised on <see cref="PairingRequested"/>.</summary>
    /// <param name="token">The token from the prompt.</param>
    /// <param name="accept">Whether the user accepted.</param>
    /// <param name="pin">The PIN typed by the user, for the provide-pin ceremony.</param>
    public void RespondToPairing(uint token, bool accept, string? pin)
    {
        Log.Info($"Bluetooth pairing: answering token {token} with "
            + $"{(accept ? "accept" : "decline")}{(pin is { Length: > 0 } ? " and a PIN" : "")}.");
        var status = NativeRadio.RespondToPairing(token, accept ? 1 : 0, pin);
        if (status != NativeRadio.Ok)
        {
            Log.Warn($"Bluetooth pairing: reply to token {token} failed: {NativeRadio.LastError()}");
        }
    }

    private void FinishPairing()
    {
        if (_pairingEntry is not null)
        {
            _pairingEntry.Busy = false;
            _pairingEntry = null;
        }
        if (_pairingHandle.IsAllocated)
        {
            _pairingHandle.Free();
        }
    }

    private static RadioManager? FromContext(nint context) =>
        context != 0 && GCHandle.FromIntPtr(context).Target is RadioManager manager
            ? manager
            : null;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnPairingRequested(
        nint context, uint token, int kind, nint pin, nint deviceName)
    {
        // Runs on a helper thread. Copy the strings before returning: they are
        // only valid for the duration of this call.
        var pinText = Marshal.PtrToStringUni(pin) ?? "";
        var name = Marshal.PtrToStringUni(deviceName) ?? "";
        // Logged on arrival, before anything can go wrong with it: this callback
        // crossing from the helper is the step that cannot be observed any other
        // way, and its absence is the difference between "Windows never asked"
        // and "we never showed the question".
        Log.Info($"Bluetooth pairing: question received (token {token}, kind {kind}, "
            + $"pin '{pinText}') for {name}.");
        var manager = FromContext(context);
        if (manager is null)
        {
            Log.Warn("Bluetooth pairing: question arrived with no manager attached; ignoring.");
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            var handled = manager.PairingRequested is not null;
            Log.Info($"Bluetooth pairing: prompting the user (token {token}, "
                + $"handler attached: {handled}).");
            manager.PairingRequested?.Invoke(new PairingPrompt(token, kind, pinText, name));
            if (!handled)
            {
                // Nobody can answer, so decline rather than leave the ceremony
                // waiting until it times out.
                Log.Warn($"Bluetooth pairing: no UI attached, declining token {token}.");
                manager.RespondToPairing(token, accept: false, null);
            }
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnPairingDone(nint context, int outcome, nint message)
    {
        var text = Marshal.PtrToStringUni(message) ?? "";
        var manager = FromContext(context);
        if (manager is null)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            var entry = manager._pairingEntry;
            var name = entry?.Name ?? "device";
            // Same reasoning as unpair: apply the outcome we already know rather
            // than leaving the row stale until the next inquiry finishes.
            if (entry is not null && outcome is 0 or 1)
            {
                entry.Paired = true;
            }
            manager.FinishPairing();
            var summary = DescribePairOutcome(outcome, name, text);
            // The raw status rides along: the grouped outcome deliberately
            // lumps rare statuses, and remote diagnosis needs the exact one.
            Log.Info($"Bluetooth pairing: finished for {name} (outcome {outcome}"
                + $"{(text.Length > 0 ? $", {text}" : "")}). {summary}");
            manager.StatusText = outcome is 0 or 1 ? "" : summary;
            manager.PairingFinished?.Invoke(summary);
        });
    }

    /// <summary>The message for a finished pairing attempt.</summary>
    internal static string DescribePairOutcome(int outcome, string device, string message) =>
        outcome switch
        {
            0 => $"{device} is paired.",
            1 => $"{device} was already paired.",
            2 => $"Pairing with {device} was cancelled.",
            3 => $"Could not pair with {device}. Make sure it is in pairing mode.",
            // The broker runs unelevated and may be unable to inspect an
            // elevated caller; that is a different problem from a sulky device.
            4 => $"Windows denied pairing with {device}.",
            // A hung earlier ceremony inside the Device Association service —
            // it survives WSGM, so only the radio (or a reboot) can clear it.
            6 => $"Windows is still busy with an earlier pairing attempt for {device}. "
                + "Turn Bluetooth off and on, then try again.",
            -1 => message.Length > 0 ? message : $"Pairing with {device} failed.",
            _ => $"Pairing with {device} did not complete.",
        };

    private void Set(ref string field, string value, string name)
    {
        if (field != value)
        {
            field = value;
            Raise(name);
        }
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (field != value)
        {
            field = value;
            Raise(name);
        }
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
