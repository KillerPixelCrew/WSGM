using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The backend behind the revealed Wi-Fi surface: scan lifetime, the scanned-network projection,
///     the polled header indicator, and joining, leaving and forgetting from Steam's page.
/// </summary>
/// <remarks>
///     The radio manager is borrowed rather than owned. Joins, disconnects and forgets go through it,
///     the same operations the Overlay's radio panel uses; the radio toggles stay with that panel.
/// </remarks>
internal sealed class NativeQamNetworkService : ISteamNetworkBackend, IAsyncDisposable
{
    private readonly Func<bool> _indicatorActive;
    private readonly Lock _joinLock = new();
    private SteamNetworkJoin? _join;
    private long _joinAttempts;
    private readonly Timer _poll;
    private readonly Action _publish;
    private readonly Timer _publishDebounce;
    private readonly RadioManager _radios;

    /// <summary>Creates the service and starts the indicator poll.</summary>
    /// <param name="radios">The session's radio manager.</param>
    /// <param name="indicatorActive">Whether the header indicator publication is active.</param>
    /// <param name="publish">Queues a state publication; timer callbacks invoke it on a thread-pool thread.</param>
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
    public async ValueTask DisposeAsync()
    {
        // Direct unsubscribe first so a session that ends while Steam's network page is open does
        // not leave this service subscribed to a collection it no longer publishes; the sweep stop
        // still marshals to the UI thread that owns it.
        _radios.Wifi.Networks.CollectionChanged -= OnScannedNetworksChanged;
        // ReSharper disable MethodHasAsyncOverload
        _poll.Dispose();
        _publishDebounce.Dispose();
        // ReSharper restore MethodHasAsyncOverload
        await NativeQamUi.RunAsync(_radios.StopScanning).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> StartScanAsync(CancellationToken cancellationToken)
    {
        await NativeQamUi.RunAsync(() =>
        {
            _radios.Wifi.Networks.CollectionChanged -= OnScannedNetworksChanged;
            _radios.Wifi.Networks.CollectionChanged += OnScannedNetworksChanged;
            _radios.StartScanning();
        }, cancellationToken).ConfigureAwait(false);
        QueuePublication();
        return SteamUiCommandResult.Applied;
    }

    /// <inheritdoc />
    /// <remarks>The stop is always queued; this implementation does not observe the cancellation token.</remarks>
    public async Task<SteamUiCommandResult> StopScanAsync(CancellationToken cancellationToken)
    {
        await StopScanningAsync().ConfigureAwait(false);
        return SteamUiCommandResult.Applied;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Accepted once the network is found; the join then runs on its own and its outcome is
    ///     published, because a Windows join can outlast the bridge's request timeout.
    /// </remarks>
    public async Task<SteamUiCommandResult> ConnectAsync(
        string ssid,
        string? password,
        CancellationToken cancellationToken)
    {
        WindowsRadio.WifiNetworkKey? found = null;
        await NativeQamUi.RunAsync(() => found = FindNetwork(ssid), cancellationToken).ConfigureAwait(false);
        if (found is not { } network)
        {
            Log.Warn($"Steam network page: {ssid} is not in the scan results.");
            return new SteamUiCommandResult(false, $"{ssid} is no longer in range.");
        }

        long attempt;
        lock (_joinLock)
        {
            attempt = ++_joinAttempts;
            _join = new SteamNetworkJoin(attempt, ssid, true, false, null);
        }

        Log.Info($"Steam network page: joining {ssid}.");
        _ = JoinAsync(attempt, ssid, network, password);
        return SteamNetworkSurface.JoinAccepted(attempt);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        Task? operation = null;
        await NativeQamUi.RunAsync(() => operation = _radios.Wifi.DisconnectAsync(), cancellationToken)
            .ConfigureAwait(false);
        await operation!.ConfigureAwait(false);
        return await CompletedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ForgetAsync(string ssid, CancellationToken cancellationToken)
    {
        Task? operation = null;
        await NativeQamUi.RunAsync(() =>
        {
            if (FindNetwork(ssid) is { } network)
            {
                operation = _radios.Wifi.ForgetAsync(network);
            }
        }, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return new SteamUiCommandResult(false, $"{ssid} is no longer in range.");
        }

        await operation.ConfigureAwait(false);
        return await CompletedAsync().ConfigureAwait(false);
    }

    /// <summary>Unsubscribes from scan results and stops the sweep, on the UI thread.</summary>
    /// <returns>Completes after the UI-thread unsubscribe and scan-stop request, without disposing the borrowed radio manager.</returns>
    internal Task StopScanningAsync()
    {
        return NativeQamUi.RunAsync(StopScanningCore);
    }

    /// <summary>Posts the scan stop without waiting, for callers on arbitrary threads.</summary>
    internal void PostStopScanning()
    {
        Dispatcher.UIThread.Post(StopScanningCore);
    }

    private void StopScanningCore()
    {
        _radios.Wifi.Networks.CollectionChanged -= OnScannedNetworksChanged;
        _radios.StopScanning();
    }

    /// <summary>Reads the network state to publish.</summary>
    /// <param name="indicatorEnabled">Whether the connected AP joins the scanned list.</param>
    /// <returns>A detached Steam network projection; a requested status refresh completes before UI-owned rows are read.</returns>
    internal async ValueTask<SteamNetworkState?> ReadStateAsync(bool indicatorEnabled)
    {
        // A connected network may not be present in the latest scan results.
        if (indicatorEnabled)
        {
            await _radios.Wifi.RefreshStatusAsync().ConfigureAwait(false);
        }

        List<WindowsRadio.WifiNetworkKey> keys = [];
        List<SteamNetworkAccessPoint> networks = [];
        WindowsRadio.WifiNetworkKey? connected = null;
        var connectedSignal = 0;
        await NativeQamUi.RunAsync(() =>
        {
            foreach (var entry in _radios.Wifi.Networks.Where(entry => !string.IsNullOrWhiteSpace(entry.Ssid)))
            {
                keys.Add(entry.Key);
                networks.Add(new SteamNetworkAccessPoint(
                    entry.Ssid,
                    SteamNetworkSurface.StrengthFromPercent(entry.Signal),
                    entry.Secured,
                    entry.Connected));
            }

            if (_radios.Wifi.Connected)
            {
                connected = _radios.Wifi.ConnectedNetwork;
                connectedSignal = _radios.Wifi.Signal;
            }
        }).ConfigureAwait(false);

        SteamNetworkJoin? join;
        lock (_joinLock)
        {
            join = _join;
        }

        if (!indicatorEnabled
            || connected is not { } network
            || string.IsNullOrWhiteSpace(network.DisplayText))
        {
            return new SteamNetworkState(networks, join);
        }

        var existing = keys.IndexOf(network);
        var joined = new SteamNetworkAccessPoint(
            network.DisplayText,
            SteamNetworkSurface.StrengthFromPercent(connectedSignal),
            existing < 0 || networks[existing].Secured,
            true);
        if (existing >= 0)
        {
            networks[existing] = joined;
        }
        else
        {
            networks.Insert(0, joined);
        }

        return new SteamNetworkState(networks, join);
    }

    private async Task JoinAsync(
        long attempt,
        string ssid,
        WindowsRadio.WifiNetworkKey network,
        string? password)
    {
        var joined = false;
        string? error = null;
        try
        {
            Task<bool>? operation = null;
            await NativeQamUi.RunAsync(() => operation = _radios.Wifi.ConnectAsync(network, password))
                .ConfigureAwait(false);
            joined = await operation!.ConfigureAwait(false);
            if (joined)
            {
                // Steam's connecting dialog closes only when the joined row reads connected, so the
                // status is current before the outcome is published.
                await _radios.Wifi.RefreshStatusAsync().ConfigureAwait(false);
            }
            else
            {
                await NativeQamUi.RunAsync(() => error = _radios.StatusText).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam network page: joining {ssid} threw: {ex.Message}");
            error = ex.Message;
        }

        lock (_joinLock)
        {
            // A later attempt owns the published outcome.
            if (_join?.Attempt == attempt)
            {
                _join = new SteamNetworkJoin(
                    attempt,
                    ssid,
                    false,
                    joined,
                    joined ? null : string.IsNullOrWhiteSpace(error) ? "Could not connect." : error);
            }
        }

        QueuePublication();
    }

    /// <summary>Answers a radio operation that reports failure through the manager's status text.</summary>
    private async Task<SteamUiCommandResult> CompletedAsync()
    {
        var status = string.Empty;
        await NativeQamUi.RunAsync(() => status = _radios.StatusText).ConfigureAwait(false);
        QueuePublication();
        return string.IsNullOrEmpty(status) ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, status);
    }

    /// <summary>The listed network with this name, or the connected one when the scan has not listed it.</summary>
    private WindowsRadio.WifiNetworkKey? FindNetwork(string ssid)
    {
        if (_radios.Wifi.Networks.FirstOrDefault(entry => string.Equals(entry.Ssid, ssid, StringComparison.Ordinal))
            is { } entry)
        {
            return entry.Key;
        }

        return _radios.Wifi.ConnectedNetwork is { } connected
               && string.Equals(connected.DisplayText, ssid, StringComparison.Ordinal)
            ? connected
            : null;
    }

    private void OnPoll(object? state)
    {
        if (_indicatorActive())
        {
            _publish();
        }
    }

    private void OnScannedNetworksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueuePublication();
    }

    private void QueuePublication()
    {
        _publishDebounce.Change(
            TimeSpan.FromMilliseconds(400),
            Timeout.InfiniteTimeSpan);
    }

    private void OnPublishDebounce(object? state)
    {
        _publish();
    }
}
