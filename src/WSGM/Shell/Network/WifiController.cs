using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Owns the session's Wi-Fi connection state, the visible network list, the live Wi-Fi watch and
///     the join, disconnect and forget actions.
/// </summary>
/// <remarks>
///     A part of <see cref="RadioManager" />, which owns the radio power, the refresh that reads this
///     state and the panel's status line. Invoke lifecycle and row-mutating members on the Avalonia UI
///     thread; native work runs on workers and the watch posts to the dispatcher. Dispose stops the
///     watch and suppresses later status publication.
/// </remarks>
public sealed class WifiController : ObservableObject, IDisposable
{
    private readonly Action _queueRefresh;
    private readonly Action<string> _reportStatus;

    /// <summary>Non-zero while a connection attempt is in flight.</summary>
    private int _connecting;

    private volatile bool _disposed;

    /// <summary>This controller's Wi-Fi watch once it has started. UI thread only.</summary>
    private IDisposable? _watch;

    /// <summary>Whether the owner wants the live watch running. UI thread only.</summary>
    private bool _watching;

    /// <summary>Creates the Wi-Fi part of a radio manager.</summary>
    /// <param name="reportStatus">Shows a line on the radio panel's shared status; UI thread.</param>
    /// <param name="queueRefresh">Queues the owner's radio refresh; UI thread.</param>
    internal WifiController(Action<string> reportStatus, Action queueRefresh)
    {
        _reportStatus = reportStatus;
        _queueRefresh = queueRefresh;
    }

    /// <summary>Gets the Wi-Fi networks in range, strongest first.</summary>
    public ObservableCollection<WifiNetworkEntry> Networks { get; } = [];

    /// <summary>
    ///     Gets whether Wi-Fi is joined to a network — the only state that
    ///     tints the sheet's Wi-Fi pill with the accent color.
    /// </summary>
    public bool Connected
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(Connected));
    }

    /// <summary>
    ///     Gets the joined network's signal quality, 0-100. Drives the bars
    ///     on the sheet's Wi-Fi pill.
    /// </summary>
    public int Signal
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(Signal));
    }

    /// <summary>Gets the joined network's name, or an empty string.</summary>
    public string ConnectedSsid
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(ConnectedSsid));
    } = "";

    /// <summary>Gets the joined network's identity, or null when Wi-Fi is not joined to one.</summary>
    internal WindowsRadio.WifiNetworkKey? ConnectedNetwork { get; private set; }

    /// <summary>Stops the live watch and suppresses later status publication. Idempotent.</summary>
    public void Dispose()
    {
        _disposed = true;
        StopFeed();
    }

    /// <summary>Reads the Wi-Fi interface status, or an unknown, unjoined one when it cannot be read.</summary>
    /// <returns>The interface status; read failures are logged once per distinct message.</returns>
    internal static WindowsRadio.WifiStatus ReadStatus()
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
    /// <returns>
    ///     Completes after the worker read and UI publication; disposal suppresses publication and read failures publish
    ///     unknown state.
    /// </returns>
    internal async Task RefreshStatusAsync()
    {
        var status = await Task.Run(ReadStatus).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                ApplyStatus(status);
            }
        });
    }

    /// <summary>Publishes an interface status read. UI thread only.</summary>
    /// <param name="status">The status the owner's refresh or <see cref="RefreshStatusAsync" /> read.</param>
    internal void ApplyStatus(WindowsRadio.WifiStatus status)
    {
        Connected = status.State == WindowsRadio.WifiConnectionState.Connected;
        // Straight from the interface, so the pill has bars whether or not the
        // panel has ever been opened.
        Signal = status.Signal;
        ConnectedSsid = status.Ssid;
        ConnectedNetwork = status.Key;
    }

    /// <summary>
    ///     Starts this controller's Wi-Fi watch off the UI thread, because opening a WLAN handle is a
    ///     service round trip. Its events only queue a refresh, so it needs no identity check.
    ///     UI thread only. Idempotent.
    /// </summary>
    internal void StartFeed()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
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
                // The feed stopped, or restarted with a watch of its own, while this one started.
                if (_disposed || !_watching || _watch is not null)
                {
                    DisposeWatch(registration);
                    return;
                }

                _watch = registration;
            });
        });
    }

    /// <summary>Stops the live Wi-Fi watch. UI thread only. Idempotent.</summary>
    internal void StopFeed()
    {
        _watching = false;
        if (_watch is not { } watch)
        {
            return;
        }

        _watch = null;
        DisposeWatch(watch);
    }

    /// <summary>
    ///     Disposes a Wi-Fi watch off the UI thread: the unregister waits for a callback that is
    ///     running, and never runs inside one.
    /// </summary>
    private static void DisposeWatch(IDisposable registration)
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

    private void OnWifiEvent(WindowsRadio.WifiWatchEvent change)
    {
        // A scan-list refresh means new networks are visible right now; a
        // connection change means the "connected" marker moved.
        Dispatcher.UIThread.Post(_queueRefresh);
        if (change == WindowsRadio.WifiWatchEvent.ConnectionChanged)
        {
            Log.Info("Wi-Fi: connection state changed.");
        }
    }

    /// <summary>
    ///     Merges a fresh network list into the bound collection without
    ///     replacing surviving rows — a wholesale rebuild would move focus out from
    ///     under the gamepad cursor mid-scan. UI thread only.
    /// </summary>
    /// <param name="fresh">A successful listing, strongest first.</param>
    internal void ReconcileNetworks(IReadOnlyList<WindowsRadio.WifiNetwork> fresh)
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
        // status read with the same refresh is authoritative and already correct;
        // no row being marked connected (a hidden network, or a scan refresh
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

    /// <summary>Joins a network, installing a profile with the password first.</summary>
    /// <param name="network">The network to join, as its row's <see cref="WifiNetworkEntry.Key" />.</param>
    /// <param name="password">The password, or null for an open or saved network.</param>
    /// <returns>
    ///     True when the network was actually joined; false leaves a
    ///     reason in <see cref="RadioManager.StatusText" />.
    /// </returns>
    public async Task<bool> ConnectAsync(WindowsRadio.WifiNetworkKey network, string? password)
    {
        var ssid = network.DisplayText;
        // Serialize profile mutation and completion watchers for this controller.
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
        {
            Log.Info($"Wi-Fi connect: {ssid} ignored, an attempt is already running.");
            _reportStatus("Still working on the last connection attempt...");
            return false;
        }

        try
        {
            _reportStatus($"Connecting to {ssid}...");
            var result = await Task.Run(() => WindowsRadio.ConnectWifi(network, password));
            switch (result.Outcome)
            {
                case WindowsRadio.WifiConnectOutcome.Joined:
                    Log.Info($"Wi-Fi connect: {ssid} connected.");
                    _reportStatus("");
                    _queueRefresh();
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
                    _reportStatus(DescribeConnectResult(result));
                    return false;
            }

            _reportStatus(DescribeConnectResult(result));
            _queueRefresh();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Wi-Fi connect: {ssid} threw: {ex.Message}");
            _reportStatus(DescribeConnectFailure(
                WindowsRadio.WifiFailureKind.Unknown, 0, ex.Message));
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
    /// <returns>
    ///     Completion of the disconnect request. Failures update the status line; it does not wait for a
    ///     disconnected-state observation.
    /// </returns>
    public async Task DisconnectAsync()
    {
        try
        {
            await Task.Run(WindowsRadio.DisconnectWifi);
            Log.Info("Wi-Fi disconnect: requested.");
            _reportStatus("");
        }
        catch (Exception ex)
        {
            _reportStatus(RadioManager.DescribeCommandFailure("disconnect from this network", ex));
        }

        _queueRefresh();
    }

    /// <summary>Deletes every saved profile of a network, so it stops joining automatically.</summary>
    /// <param name="network">The network to forget, as its row's <see cref="WifiNetworkEntry.Key" />.</param>
    /// <returns>Completion of the deletion attempts. Partial failures update the status line; a refresh is queued afterward.</returns>
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
                _reportStatus($"Could not forget {ssid}.");
            }
            else
            {
                Log.Info($"Wi-Fi forget: {ssid}.");
                _reportStatus("");
            }
        }
        catch (Exception ex)
        {
            _reportStatus(RadioManager.DescribeCommandFailure($"forget {ssid}", ex));
        }

        _queueRefresh();
    }
}
