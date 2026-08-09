using System;
using System.Threading;
using Avalonia.Threading;
using WSGM.Interop;
using WSGM.Shell;

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

    /// <summary>Drives a real pairing through the MANAGED path, auto-answering
    /// the question instead of showing UI.
    ///
    /// The helper's own probe already proves the Rust side pairs in about a
    /// second. What it cannot prove is the part unique to this process: the
    /// callback crossing the ABI into a NativeAOT static, the hop onto the
    /// Avalonia dispatcher, and the reply going back. That is the stretch where
    /// a pairing was observed to hang forever, so it gets exercised directly.
    /// </summary>
    /// <param name="needle">Part of the device name to pair with.</param>
    internal static void ProbePairing(string needle)
    {
        Log.Info($"Radio probe: pairing test against a device matching '{needle}'.");
        using var finished = new ManualResetEventSlim(false);
        var manager = new RadioManager();

        // Found through the live watcher, never the blocking list: that
        // enumeration runs a real inquiry and takes ~30 s, by which time a
        // controller has left pairing mode and the managed callback path this
        // probe exists to exercise is never reached at all. The panel and the
        // native probe both discover this way for the same reason.
        BluetoothDeviceEntry? found = null;
        manager.StartScanning();
        var searchDeadline = DateTime.UtcNow.AddSeconds(20);
        while (found is null && DateTime.UtcNow < searchDeadline)
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var candidate in manager.BluetoothDevices)
            {
                if (!candidate.Paired && candidate.CanPair
                    && (needle.Length == 0
                        || candidate.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                {
                    found = candidate;
                    break;
                }
            }
            Thread.Sleep(50);
        }
        if (found is null)
        {
            manager.StopScanning();
            manager.Dispose();
            Log.Warn($"Radio probe: no unpaired, pairable device matching '{needle}'.");
            return;
        }
        var targetName = found.Name;

        Log.Info($"Radio probe: pairing with {targetName}.");
        var answered = false;
        manager.PairingRequested += prompt =>
        {
            answered = true;
            Log.Info($"Radio probe: question reached the UI layer (kind {prompt.Kind}).");
            manager.RespondToPairing(prompt.Token, accept: true, prompt.Kind == 2 ? "0000" : null);
        };
        manager.PairingFinished += summary =>
        {
            Log.Info($"Radio probe: pairing finished: {summary}");
            finished.Set();
        };
        manager.BeginPairing(found);

        // The dispatcher must keep running or the posted callbacks never arrive,
        // which is itself one of the things being tested.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!finished.IsSet && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }
        Log.Info(finished.IsSet
            ? "Radio probe: pairing test completed."
            : $"Radio probe: pairing test TIMED OUT (question reached the UI: {answered}).");
        manager.StopScanning();
        manager.Dispose();
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
