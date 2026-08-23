using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Keeps Big Picture's header Wi-Fi indicator fed with real state: polls the
/// native radio helper (the same <see cref="NativeRadio.WifiStatus"/> the taskbar tile
/// uses) and pushes changes into Steam via <see cref="SteamNetworkIndicator"/>.
///
/// A CEF evaluation only goes out when the pushed tuple (connected, SSID, strength
/// band) changes, when the last push failed (Steam not up yet), or on a periodic heal
/// — a Steam restart wipes the resident script, and <see cref="Poke"/> shortcuts that
/// wait when the caller knows Steam just came back. Game-mode only; the owner disposes
/// it on desktop transitions alongside the tabs/badge kill switches.</summary>
public sealed class NetworkIndicatorService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealInterval = TimeSpan.FromMinutes(3);

    private readonly CancellationTokenSource _cts = new();
    private int _poked;
    private bool _waitingForSteamUi;

    private NetworkIndicatorService()
    {
    }

    /// <summary>Starts the poll loop and returns the running service.</summary>
    public static NetworkIndicatorService StartNew()
    {
        var service = new NetworkIndicatorService();
        _ = Task.Run(service.RunAsync);
        return service;
    }

    /// <summary>Forces the next tick to push even if nothing changed — call when
    /// Steam (re)started, so the indicator heals immediately instead of waiting
    /// out the heal interval.</summary>
    public void Poke() => Interlocked.Exchange(ref _poked, 1);

    private async Task RunAsync()
    {
        var token = _cts.Token;
        (bool Connected, string Ssid, int Band) last = default;
        var lastOk = false;
        var lastPush = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!SteamUiReadiness.IsReady)
                {
                    if (!_waitingForSteamUi)
                    {
                        _waitingForSteamUi = true;
                        Log.Info("Network indicator: waiting for the Big Picture window.");
                    }
                }
                else
                {
                    if (_waitingForSteamUi)
                    {
                        _waitingForSteamUi = false;
                        Log.Info("Network indicator: Big Picture is ready; starting the feed.");
                    }
                    var connected = false;
                    var ssid = "";
                    var signal = 0;
                    if (NativeRadio.WifiStatus(out var state, out var quality, out var name)
                            == NativeRadio.Ok
                        && state == 0 && !string.IsNullOrEmpty(name))
                    {
                        connected = true;
                        ssid = name;
                        signal = quality;
                    }

                    var tuple = (
                        connected,
                        ssid,
                        connected ? SteamNetworkIndicator.MapStrength(signal) : 0);
                    var poked = Interlocked.Exchange(ref _poked, 0) == 1;
                    if (tuple != last || !lastOk || poked
                        || DateTime.UtcNow - lastPush >= HealInterval)
                    {
                        lastOk = await SteamNetworkIndicator.PushAsync(connected, ssid, signal, token)
                            .ConfigureAwait(false);
                        if (lastOk)
                        {
                            if (tuple != last)
                            {
                                Log.Info(connected
                                    ? $"Network indicator: '{ssid}' at {signal}% (bars {tuple.Item3}/4)."
                                    : "Network indicator: not connected — cleared.");
                            }
                            last = tuple;
                            lastPush = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Network indicator tick failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Stops the poll loop. Does not touch Steam — callers pair this with
    /// <see cref="SteamNetworkIndicator.DisableAsync"/> when leaving game mode.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
