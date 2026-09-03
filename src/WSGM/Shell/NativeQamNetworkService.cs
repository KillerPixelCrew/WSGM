using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>
/// The backend behind the revealed Wi-Fi surface: scan lifetime, the scanned-network projection,
/// and the polled header indicator.
/// </summary>
/// <remarks>
/// The radio manager is borrowed rather than owned — only its scanning lifetime is driven from
/// here. Joining, forgetting and the radio toggles stay with the surfaces that already own them.
/// </remarks>
internal sealed class NativeQamNetworkService : ISteamNetworkBackend, IAsyncDisposable
{
    private readonly RadioManager _radios;
    private readonly Func<bool> _indicatorActive;
    private readonly Action _publish;
    private readonly Timer _poll;
    private readonly Timer _publishDebounce;

    /// <summary>Creates the service and starts the indicator poll.</summary>
    /// <param name="radios">The session's radio manager.</param>
    /// <param name="indicatorActive">Whether the header indicator publication is active.</param>
    /// <param name="publish">Queues a state publication toward Steam.</param>
    internal NativeQamNetworkService(
        RadioManager radios,
        Func<bool> indicatorActive,
        Action publish)
    {
        _radios = radios;
        _indicatorActive = indicatorActive;
        _publish = publish;
        _poll = new Timer(OnPoll, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        _publishDebounce = new Timer(
            OnPublishDebounce,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> StartScanAsync(CancellationToken cancellationToken)
    {
        await NativeQamUi.RunAsync(() =>
        {
            _radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
            _radios.Networks.CollectionChanged += OnScannedNetworksChanged;
            _radios.StartScanning();
        }).ConfigureAwait(false);
        QueuePublication();
        return SteamUiCommandResult.Applied;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> StopScanAsync(CancellationToken cancellationToken)
    {
        await StopScanningAsync().ConfigureAwait(false);
        return SteamUiCommandResult.Applied;
    }

    /// <summary>Unsubscribes from scan results and stops the sweep, on the UI thread.</summary>
    internal Task StopScanningAsync() => NativeQamUi.RunAsync(() =>
    {
        _radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
        _radios.StopScanning();
    });

    /// <summary>Posts the scan stop without waiting, for callers on arbitrary threads.</summary>
    internal void PostStopScanning() => Dispatcher.UIThread.Post(() =>
    {
        _radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
        _radios.StopScanning();
    });

    /// <summary>Reads the network state to publish.</summary>
    /// <param name="indicatorEnabled">Whether the connected AP joins the scanned list.</param>
    internal async ValueTask<SteamNetworkState?> ReadStateAsync(bool indicatorEnabled)
    {
        List<SteamNetworkAccessPoint> networks = [];
        await NativeQamUi.RunAsync(() =>
        {
            foreach (WifiNetworkEntry entry in _radios.Networks.Take(24))
            {
                if (!string.IsNullOrWhiteSpace(entry.Ssid))
                {
                    networks.Add(new SteamNetworkAccessPoint(
                        entry.Ssid,
                        SteamNetworkSurface.StrengthFromPercent(entry.Signal),
                        entry.Secured,
                        entry.Connected));
                }
            }
        }).ConfigureAwait(false);

        // The connected network is merged in from the live status rather than the scan list, which
        // is what makes the header Wi-Fi indicator show a signal on Windows at all.
        WindowsRadio.WifiStatus connected = indicatorEnabled
            ? WindowsRadio.GetWifiStatus()
            : default;
        if (indicatorEnabled
            && connected.State == 0
            && !string.IsNullOrWhiteSpace(connected.Ssid))
        {
            int existing = networks.FindIndex(network =>
                string.Equals(network.Ssid, connected.Ssid, StringComparison.Ordinal));
            var joined = new SteamNetworkAccessPoint(
                connected.Ssid,
                SteamNetworkSurface.StrengthFromPercent(connected.Signal),
                existing >= 0 ? networks[existing].Secured : true,
                true);
            if (existing >= 0)
            {
                networks[existing] = joined;
            }
            else
            {
                networks.Insert(0, joined);
                if (networks.Count > 24)
                {
                    networks.RemoveAt(networks.Count - 1);
                }
            }
        }

        return new SteamNetworkState(networks);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Direct unsubscribe first so a session that ends while Steam's network page is open does
        // not leave this service subscribed to a collection it no longer publishes; the sweep stop
        // still marshals to the UI thread that owns it.
        _radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
        _poll.Dispose();
        _publishDebounce.Dispose();
        await NativeQamUi.RunAsync(_radios.StopScanning).ConfigureAwait(false);
    }

    private void OnPoll(object? state)
    {
        if (_indicatorActive())
        {
            _publish();
        }
    }

    private void OnScannedNetworksChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueuePublication();

    private void QueuePublication() =>
        _publishDebounce.Change(
            TimeSpan.FromMilliseconds(400),
            Timeout.InfiniteTimeSpan);

    private void OnPublishDebounce(object? state) => _publish();
}
