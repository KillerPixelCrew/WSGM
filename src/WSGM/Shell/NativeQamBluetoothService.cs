using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;

namespace WSGM.Shell;

/// <summary>
/// The backend behind Steam's own Bluetooth pairing UI, reading and driving the session's radio
/// manager.
/// </summary>
/// <remarks>
/// Pairing opens the session's prompt surface before dispatching through <see cref="RadioManager"/>.
/// Device identity, operation state and completion remain shared with the Overlay.
/// </remarks>
internal sealed class NativeQamBluetoothService : ISteamBluetoothBackend
{
    private readonly RadioManager _radios;
    private readonly Func<bool>? _showBluetoothPanel;

    /// <summary>Creates the service over the session's radio manager.</summary>
    internal NativeQamBluetoothService(RadioManager radios, Func<bool>? showBluetoothPanel = null)
    { _radios = radios; _showBluetoothPanel = showBluetoothPanel; }

    /// <summary>
    /// Reads the radio manager's Bluetooth view into the shape Steam's panel consumes.
    /// </summary>
    /// <returns>The state to publish.</returns>
    /// <remarks>
    /// Reported unavailable when the radio is off rather than as an empty device list. Steam's panel
    /// distinguishes the two — "Bluetooth is off" is a state a user can act on, while an empty list
    /// reads as "nothing found" and invites them to keep waiting for devices that will never arrive.
    /// </remarks>
    internal async ValueTask<SteamBluetoothState?> ReadStateAsync()
    {
        List<SteamBluetoothDevice> devices = [];
        bool available = false;
        bool enabled = false;
        bool discovering = false;
        await NativeQamUi.RunAsync(() =>
        {
            // Available means "this machine has a Bluetooth radio WSGM can drive", never "the radio
            // is on". Wiring it to the on/off state made turning Bluetooth off remove the entire
            // settings page and the toggle with it — the exact control needed to turn it back on.
            available = _radios.BluetoothPower
                is not RadioPower.Absent and not RadioPower.Disabled;
            enabled = _radios.BluetoothOn;
            discovering = _radios.BluetoothScanning;
            foreach (BluetoothDeviceEntry entry in _radios.BluetoothDevices)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }

                devices.Add(new SteamBluetoothDevice(
                    entry.Id,
                    string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
                    entry.Id,
                    // Steam's generic device type. WSGM does not classify Bluetooth devices, and a
                    // guessed class would put the wrong icon beside a real device.
                    0,
                    entry.Paired,
                    entry.AudioConnectable ? entry.AudioActive : entry.Connected)
                { OperationInProgress = entry.Busy });
            }
        }).ConfigureAwait(false);

        return new SteamBluetoothState(available, enabled, discovering, devices);
    }

    /// <inheritdoc />
    /// <remarks>
    /// BluetoothScanning is manager-owned and driven by the same sweep as Wi-Fi, so discovery goes
    /// through the scanning lifecycle rather than being set directly. One sweep covering both
    /// radios is also what the taskbar's panel does.
    /// </remarks>
    public async Task<SteamUiCommandResult> SetDiscoveringAsync(
        bool discovering,
        CancellationToken cancellationToken)
    {
        await NativeQamUi.RunAsync(() =>
        {
            _radios.SetSteamDiscovery(discovering);
        }, cancellationToken).ConfigureAwait(false);
        return new(true, null);
    }

    /// <inheritdoc />
    /// <remarks>The existing radio panel hosts PIN and confirmation prompts.</remarks>
    public async Task<SteamUiCommandResult> PairAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        bool started = false;
        await NativeQamUi.RunAsync(() =>
        {
            if (_showBluetoothPanel?.Invoke() != true)
            { _radios.ReportStatus("The Bluetooth pairing prompt is unavailable."); return; }
            started = _radios.BeginPairing(device);
        }, cancellationToken).ConfigureAwait(false);
        return new(started, started ? null : _radios.StatusText);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> CancelPairAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        bool cancelled = false;
        await NativeQamUi.RunAsync(() => cancelled = _radios.CancelPairing(device), cancellationToken).ConfigureAwait(false);
        return new(cancelled, cancelled ? null : "That device has no active pairing attempt.");
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ConnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        return await SetConnectionAsync(device, true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> DisconnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        return await SetConnectionAsync(device, false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ForgetAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        Task<bool>? operation = null;
        await NativeQamUi.RunAsync(() =>
        {
            _showBluetoothPanel?.Invoke();
            operation = _radios.UnpairAsync(device);
        }, cancellationToken).ConfigureAwait(false);
        bool removed = await operation!.ConfigureAwait(false);
        return new(removed, removed ? null : _radios.StatusText);
    }

    /// <inheritdoc />
    /// <remarks>A BlueZ concept with no Windows equivalent; accepted so Steam's UI does not report
    /// a failure for a control that was never going to change anything.</remarks>
    public Task<SteamUiCommandResult> SetTrustedAsync(
        string deviceId,
        bool trusted,
        CancellationToken cancellationToken) =>
        AcceptWithoutEquivalent("setTrusted");

    /// <inheritdoc />
    /// <remarks>See <see cref="SetTrustedAsync"/>.</remarks>
    public Task<SteamUiCommandResult> SetWakeAllowedAsync(
        string deviceId,
        bool allowed,
        CancellationToken cancellationToken) =>
        AcceptWithoutEquivalent("setWakeAllowed");

    private static Task<SteamUiCommandResult> AcceptWithoutEquivalent(string command)
    {
        Log.Info($"Bluetooth: '{command}' accepted with no Windows equivalent.");
        return Task.FromResult(new SteamUiCommandResult(true, null));
    }

    private async Task<BluetoothDeviceEntry?> FindAsync(string deviceId)
    {
        BluetoothDeviceEntry? device = null;
        await NativeQamUi.RunAsync(() => device = _radios.BluetoothDevices.FirstOrDefault(entry =>
            string.Equals(entry.Id, deviceId, StringComparison.Ordinal))).ConfigureAwait(false);
        return device;
    }

    private async Task<SteamUiCommandResult> SetConnectionAsync(BluetoothDeviceEntry device, bool connect, CancellationToken cancellationToken)
    {
        Task<bool>? operation = null;
        await NativeQamUi.RunAsync(() =>
        {
            _showBluetoothPanel?.Invoke();
            operation = _radios.SetAudioConnectionAsync(device, connect, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        bool confirmed = await operation!.ConfigureAwait(false);
        return new(confirmed, confirmed ? null : _radios.StatusText);
    }

    private static SteamUiCommandResult Absent(string deviceId)
    {
        Log.Warn($"Bluetooth: '{deviceId}' is no longer present.");
        return new(false, "That device is no longer present.");
    }

    internal Task StopDiscoveryAsync() => NativeQamUi.RunAsync(() => _radios.SetSteamDiscovery(false));
}
