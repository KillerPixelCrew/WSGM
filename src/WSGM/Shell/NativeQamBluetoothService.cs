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
/// Pairing has no direct call: <see cref="RadioManager"/> drives it through a prompt the user
/// answers, and inventing a headless pair here would either bypass a PIN confirmation the device
/// requires or silently fail on one that does. Steam's Pair button therefore starts discovery and
/// lets the existing prompt flow run, which is the same path the taskbar uses.
/// </remarks>
internal sealed class NativeQamBluetoothService : ISteamBluetoothBackend
{
    private readonly RadioManager _radios;

    /// <summary>Creates the service over the session's radio manager.</summary>
    internal NativeQamBluetoothService(RadioManager radios) => _radios = radios;

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
                    entry.Connected));
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
            if (discovering)
            {
                _radios.StartScanning();
            }
            else
            {
                _radios.StopScanning();
            }
        }).ConfigureAwait(false);
        return new(true, null);
    }

    /// <inheritdoc />
    /// <remarks>Discovery drives the prompt; the user answers it exactly as they do from the
    /// taskbar's radio panel.</remarks>
    public async Task<SteamUiCommandResult> PairAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is null)
        {
            return Absent(deviceId);
        }

        await NativeQamUi.RunAsync(_radios.StartScanning).ConfigureAwait(false);
        return new(true, null);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> CancelPairAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is null)
        {
            return Absent(deviceId);
        }

        await NativeQamUi.RunAsync(_radios.StopScanning).ConfigureAwait(false);
        return new(true, null);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ConnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        await _radios.SetAudioConnectionAsync(device, connect: true).ConfigureAwait(false);
        return new(true, null);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> DisconnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        await _radios.SetAudioConnectionAsync(device, connect: false).ConfigureAwait(false);
        return new(true, null);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ForgetAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (await FindAsync(deviceId).ConfigureAwait(false) is not { } device)
        {
            return Absent(deviceId);
        }

        await _radios.UnpairAsync(device).ConfigureAwait(false);
        return new(true, null);
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

    private static SteamUiCommandResult Absent(string deviceId)
    {
        Log.Warn($"Bluetooth: '{deviceId}' is no longer present.");
        return new(false, "That device is no longer present.");
    }
}
