using System;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>A read-only diagnostic that writes what the radio subsystem can
/// actually do on this machine into the log.
///
/// It exists because three things cannot be settled from documentation, and
/// this project's device is only reachable through pasted logs:
///
/// * whether WinRT radio control works from an elevated process with no
///   Explorer shell in the session,
/// * whether the Windows 11 24H2 precise-location gate blocks the Wi-Fi scan,
/// * whether the managed binding and its NativeAOT callbacks survive the AOT
///   publish at all — the Rust-side probe cannot answer that one, because it
///   never crosses this boundary.
///
/// Strictly read-only. It never changes a radio's state and never writes a
/// consent value, so running it can never be what breaks a session.</summary>
public static class RadioProbe
{
    /// <summary>Runs every check and writes the result to the log.</summary>
    /// <returns>Zero. The verdict is the log, not the exit code — the point is
    /// to gather evidence, not to gate anything on it.</returns>
    public static int Run()
    {
        Log.Info("---- radio probe ----");
        // Both are the conditions the open questions are about, so they belong
        // in the same log block as the answers.
        Log.Info($"Radio probe: elevated={ElevationCheck.IsCurrentProcessElevated()}, "
            + $"explorer={ExplorerControl.IsRunningInSession()}");

        ProbeRadio("Wi-Fi", 0);
        ProbeRadio("Bluetooth", 1);
        ProbeAccess();
        ProbeConsent("location");
        ProbeConsent("radios");
        ProbeWifi();
        ProbeBluetooth();
        ProbeTouchKeyboard();

        Log.Info("---- radio probe done ----");
        return 0;
    }

    private static void ProbeRadio(string label, int kind)
    {
        try
        {
            var status = NativeRadio.GetRadioPower(kind, out var state);
            Log.Info(status == NativeRadio.Ok
                ? $"Radio probe: {label} radio power={DescribePower(state)}"
                : $"Radio probe: {label} radio power FAILED: {NativeRadio.LastError()}");
        }
        catch (Exception ex)
        {
            // A missing or unloadable helper is the single most likely failure
            // on a fresh install, and it must be legible in the log.
            Log.Warn($"Radio probe: {label} radio power threw: {ex.Message}");
        }
    }

    private static void ProbeAccess()
    {
        try
        {
            var status = NativeRadio.RequestRadioAccess(out var access);
            Log.Info(status == NativeRadio.Ok
                ? $"Radio probe: radio control access={DescribeAccess(access)}"
                : $"Radio probe: radio control access FAILED: {NativeRadio.LastError()}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: radio access threw: {ex.Message}");
        }
    }

    private static void ProbeConsent(string capability)
    {
        try
        {
            var status = NativeRadio.GetConsent(capability, out var user, out var machine);
            Log.Info(status == NativeRadio.Ok
                ? $"Radio probe: consent {capability} user={DescribeConsent(user)} machine={DescribeConsent(machine)}"
                : $"Radio probe: consent {capability} FAILED: {NativeRadio.LastError()}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: consent {capability} threw: {ex.Message}");
        }
    }

    private static void ProbeWifi()
    {
        try
        {
            var status = NativeRadio.GetWifiState(out var state);
            Log.Info(status == NativeRadio.Ok
                ? $"Radio probe: wlan interface state={state} (0 connected, 1 connecting, 2 disconnected, 3 unavailable)"
                : $"Radio probe: wlan interface state FAILED: {NativeRadio.LastError()}");

            var scan = NativeRadio.RequestWifiScan();
            Log.Info(scan == NativeRadio.Ok
                ? "Radio probe: wlan scan accepted"
                : $"Radio probe: wlan scan FAILED: {NativeRadio.LastError()}");

            if (NativeRadio.ListWifiNetworks(out var items, out var count) != NativeRadio.Ok)
            {
                var error = NativeRadio.LastError();
                Log.Warn($"Radio probe: wlan network list FAILED: {error}");
                // Worth calling out by name: this is the 24H2 gate, not a
                // permission problem that elevating would solve.
                if (error.Contains("Win32 5", StringComparison.Ordinal))
                {
                    Log.Warn("Radio probe: that is the precise-location consent gate "
                        + "(Settings > Privacy & security > Location).");
                }
                return;
            }
            Log.Info($"Radio probe: wlan network list={count} network(s)");
            for (var i = 0; i < count && i < 8; i++)
            {
                var network = NativeRadio.ReadWifiNetwork(items + (i * NativeRadio.WifiRecordSize));
                Log.Info($"Radio probe:   \"{network.Ssid}\" {network.Signal}% "
                    + $"security={network.Security} saved={network.Saved}");
            }
            NativeRadio.FreeWifiNetworks(items, count);
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: wlan threw: {ex.Message}");
        }
    }

    private static void ProbeBluetooth()
    {
        try
        {
            if (NativeRadio.ListBluetoothDevices(0, out var items, out var count) != NativeRadio.Ok)
            {
                Log.Warn($"Radio probe: bluetooth list FAILED: {NativeRadio.LastError()}");
                return;
            }
            Log.Info($"Radio probe: bluetooth list={count} device(s)");
            for (var i = 0; i < count && i < 12; i++)
            {
                var device = NativeRadio.ReadBluetoothDevice(
                    items + (i * NativeRadio.BluetoothRecordSize));
                Log.Info($"Radio probe:   \"{device.Name}\" paired={device.Paired} "
                    + $"canPair={device.CanPair}");
            }
            NativeRadio.FreeBluetoothDevices(items, count);
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: bluetooth threw: {ex.Message}");
        }
    }

    private static void ProbeTouchKeyboard()
    {
        // Text entry is what a password and a PIN both need, and whether TabTip
        // renders over a game-mode surface with no shell is unproven. Starting
        // it here is harmless and the log line is the evidence.
        TouchKeyboard.Show();
    }

    private static string DescribePower(int state) => state switch
    {
        0 => "On",
        1 => "Off",
        2 => "Disabled (policy or hardware switch)",
        4 => "Absent (no such radio)",
        _ => "Unknown",
    };

    private static string DescribeAccess(int access) => access switch
    {
        0 => "Allowed",
        1 => "DeniedByUser",
        2 => "DeniedBySystem",
        _ => "Unspecified",
    };

    private static string DescribeConsent(int consent) => consent switch
    {
        0 => "Allow",
        1 => "Deny",
        2 => "Unset",
        _ => "Unknown",
    };
}
