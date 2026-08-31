using System;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>A read-only diagnostic that writes what the radio subsystem can
/// actually do on this machine into the log.
///
/// It exists because three things cannot be settled from documentation, and
/// this project's device is only reachable through pasted logs:
///
/// * whether WinRT radio control works from an elevated process with no
///   Explorer shell in the session,
/// * whether the Windows 11 24H2 precise-location gate blocks the Wi-Fi scan,
/// * whether custom pairing and its MTA deferral complete without Explorer.
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

        ProbeRadio("Wi-Fi", WindowsRadio.RadioKind.WiFi);
        ProbeRadio("Bluetooth", WindowsRadio.RadioKind.Bluetooth);
        ProbeAccess();
        ProbeConsent("location");
        ProbeConsent("radios");
        ProbeWifi();
        ProbeBluetooth();

        Log.Info("---- radio probe done ----");
        return 0;
    }

    private static void ProbeRadio(string label, WindowsRadio.RadioKind kind)
    {
        try
        {
            var state = WindowsRadio.GetPower(kind);
            Log.Info($"Radio probe: {label} radio power={state}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: {label} radio power threw: {ex.Message}");
        }
    }

    private static void ProbeAccess()
    {
        try
        {
            var access = WindowsRadio.RequestAccess();
            Log.Info($"Radio probe: radio control access={access}");
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
            var consent = WindowsRadio.GetConsent(capability);
            Log.Info($"Radio probe: consent {capability} user={consent.User} machine={consent.Machine}");
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
            var status = WindowsRadio.GetWifiStatus();
            Log.Info($"Radio probe: wlan interface state={status.State} "
                + "(0 connected, 1 connecting, 2 disconnected, 3 unavailable)");

            WindowsRadio.RequestWifiScan();
            Log.Info("Radio probe: wlan scan accepted");

            var networks = WindowsRadio.ListWifiNetworks();
            Log.Info($"Radio probe: wlan network list={networks.Count} network(s)");
            foreach (var network in networks.Take(8))
            {
                Log.Info($"Radio probe:   \"{network.Ssid}\" {network.Signal}% "
                    + $"security={network.Security} saved={network.Saved}");
            }
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
            var devices = WindowsRadio.ListBluetoothDevices(pairedOnly: false);
            Log.Info($"Radio probe: bluetooth list={devices.Count} device(s)");
            foreach (var device in devices.Take(12))
            {
                Log.Info($"Radio probe:   \"{device.Name}\" paired={device.Paired} "
                    + $"canPair={device.CanPair}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: bluetooth threw: {ex.Message}");
        }
    }

    /// <summary>Drives a real pairing through the managed path, auto-answering
    /// the question instead of showing UI.
    ///
    /// The hop onto Avalonia and the MTA reply are the stretch where pairing was
    /// observed to hang forever, so it is exercised directly.
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
        // probe exists to exercise is never reached at all. The panel and this
        // diagnostic both discover this way for the same reason.
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
            manager.RespondToPairing(
                prompt.Token,
                accept: true,
                prompt.Kind == WindowsRadio.PairingKind.ProvidePin ? "0000" : null);
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

}
