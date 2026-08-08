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
            }
        }
    }

    /// <summary>Gets whether the Wi-Fi radio is on.</summary>
    public bool WifiOn => WifiPower == RadioPower.On;

    /// <summary>Gets whether the Bluetooth radio is on.</summary>
    public bool BluetoothOn => BluetoothPower == RadioPower.On;

    private bool _wifiConnected;
    /// <summary>Gets whether Wi-Fi is joined to a network — the only state that
    /// tints the taskbar's Wi-Fi tile with the accent color.</summary>
    public bool WifiConnected
    {
        get => _wifiConnected;
        private set => Set(ref _wifiConnected, value, nameof(WifiConnected));
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
        QueueRefresh();
    }

    /// <summary>Stops actively scanning. Idempotent.</summary>
    public void StopScanning()
    {
        if (!_scanning)
        {
            return;
        }
        _scanning = false;
        Log.Info("Radio panel: scanning stopped.");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        // The radio and Wi-Fi state reads are cheap enough for every tick; a
        // fresh driver scan is not, so it is asked for far less often.
        if (_scanning && _ticks % 5 == 0)
        {
            _ = Task.Run(() => NativeRadio.RequestWifiScan());
        }
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
        int WifiState,
        IReadOnlyList<NativeRadio.WifiNetwork> Networks,
        IReadOnlyList<NativeRadio.BluetoothDevice> Devices,
        string? Failure);

    private static Snapshot ReadSnapshot(bool includeLists)
    {
        var wifiPower = ReadPower(KindWifi);
        var bluetoothPower = ReadPower(KindBluetooth);

        var wifiState = 3;
        if (NativeRadio.GetWifiState(out var rawState) == NativeRadio.Ok)
        {
            wifiState = rawState;
        }

        IReadOnlyList<NativeRadio.WifiNetwork> networks = [];
        IReadOnlyList<NativeRadio.BluetoothDevice> devices = [];
        string? failure = null;

        if (includeLists)
        {
            if (wifiPower == RadioPower.On)
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
            if (bluetoothPower == RadioPower.On
                && NativeRadio.ListBluetoothDevices(0, out var btItems, out var btCount)
                    == NativeRadio.Ok)
            {
                devices = ReadArray(
                    btItems,
                    btCount,
                    NativeRadio.BluetoothRecordSize,
                    NativeRadio.ReadBluetoothDevice);
                NativeRadio.FreeBluetoothDevices(btItems, btCount);
            }
        }

        return new Snapshot(wifiPower, bluetoothPower, wifiState, networks, devices, failure);
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
        WifiConnected = snapshot.WifiState == 0;
        WifiStateText = DescribeWifi(snapshot.WifiPower, snapshot.WifiState);
        BluetoothStateText = DescribeBluetooth(snapshot.BluetoothPower, snapshot.Devices.Count);

        if (snapshot.Failure is { Length: > 0 } failure)
        {
            StatusText = DescribeScanFailure(failure);
        }

        ReconcileNetworks(snapshot.Networks);
        ReconcileDevices(snapshot.Devices);
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
            // The strongest saved network is the one we are on: the list is
            // sorted by signal and only a saved profile can already be joined.
            row.Connected = WifiConnected && source.Saved && i == 0;
            if (row.Connected)
            {
                connected = row.Ssid;
            }
        }
        for (var i = Networks.Count - 1; i >= fresh.Count; i--)
        {
            Networks.RemoveAt(i);
        }
        ConnectedSsid = connected;
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
    /// <returns>True when the join was accepted; false leaves a reason in
    /// <see cref="StatusText"/>.</returns>
    public async Task<bool> ConnectAsync(string ssid, string? password)
    {
        StatusText = $"Connecting to {ssid}...";
        var result = await Task.Run(() =>
        {
            var status = NativeRadio.ConnectWifi(ssid, password, out var reason);
            return (status, reason, error: NativeRadio.LastError());
        });

        if (result.status == NativeRadio.Ok)
        {
            Log.Info($"Wi-Fi connect: requested {ssid}.");
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
        await Task.Run(() => NativeRadio.DisconnectWifi());
        Log.Info("Wi-Fi disconnect: requested.");
        StatusText = "";
        QueueRefresh();
    }

    /// <summary>Deletes a saved network, so it stops joining automatically.</summary>
    /// <param name="ssid">The network to forget.</param>
    public async Task ForgetAsync(string ssid)
    {
        await Task.Run(() => NativeRadio.ForgetWifi(ssid));
        Log.Info($"Wi-Fi forget: {ssid}.");
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
        Log.Info($"Bluetooth unpair: {entry.Name} -> {removed}.");
        StatusText = removed ? "" : $"Could not remove {entry.Name}.";
        QueueRefresh();
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
        var manager = FromContext(context);
        if (manager is null)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
            manager.PairingRequested?.Invoke(new PairingPrompt(token, kind, pinText, name)));
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
            var name = manager._pairingEntry?.Name ?? "device";
            manager.FinishPairing();
            var summary = DescribePairOutcome(outcome, name, text);
            Log.Info($"Bluetooth pairing: finished for {name} (outcome {outcome}). {summary}");
            manager.StatusText = outcome is 0 or 1 ? "" : summary;
            manager.PairingFinished?.Invoke(summary);
            manager.QueueRefresh();
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
