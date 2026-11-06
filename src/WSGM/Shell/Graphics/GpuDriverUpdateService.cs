using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Owns update policy and one cancellable operation for both user surfaces, independently of device integration.</summary>
internal sealed class GpuDriverUpdateService : IAsyncDisposable, IGpuDriverUpdateSource
{
    internal const string InstallWarning = "The screen may flicker or go black while the graphics driver restarts. "
        + "Save your work, close all games and connect AC power. This accepts the vendor's driver license. "
        + "WSGM never restarts Windows automatically.";
    internal const string Rollback = "To roll back: open Windows Device Manager, expand Display adapters, open the GPU's "
        + "Properties > Driver > Roll Back Driver. If unavailable, reinstall the previous driver from your vendor or OEM.";

    private readonly ConfigStore _store;
    private readonly Func<bool?> _gameRunning;
    private readonly Func<string?> _oemRefusal;
    private readonly Action<Action> _post;
    private readonly Func<GpuVendor, bool, CancellationToken, Task> _driverTransition;
    private readonly GpuDriverUpdateCatalog _catalog = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private IReadOnlyList<GpuDriverUpdateState> _state = [];
    private Task _operation = Task.CompletedTask;
    private readonly Task _automatic;
    private CancellationTokenSource? _cancel;
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private bool _closed;
    private bool _disposed;

    internal GpuDriverUpdateService(ConfigStore store, Func<bool?> gameRunning, Func<string?> oemRefusal,
        Action<Action> post, Func<GpuVendor, bool, CancellationToken, Task>? driverTransition = null)
    {
        _store = store;
        _gameRunning = gameRunning;
        _oemRefusal = oemRefusal;
        _post = post;
        _driverTransition = driverTransition ?? ((_, _, _) => Task.CompletedTask);
        _automatic = Task.Run(AutomaticAsync);
    }

    public event Action? Changed;
    internal event Action<GpuDriverUpdateState>? UpdateFound;
    public bool Automatic => _store.Read().Config?.GpuDriverUpdatesAutoCheck == true;
    public GpuDriverInstallHistory? History => _store.Read().Config?.GpuDriverInstallHistory;
    public string? DiscoveryDetail { get; private set; } = "Reading installed graphics drivers…";

    public IReadOnlyList<GpuDriverUpdateState> Snapshot()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public void SetAutomatic(bool enabled)
    {
        _store.Update(config =>
        {
            config.GpuDriverUpdatesAutoCheck = enabled;
            return true;
        });
        Publish();
        if (enabled)
        {
            Check();
        }
    }

    /// <summary>Starts a bounded check and returns immediately so neither surface holds a bridge command open.</summary>
    public bool Check() => Start(CheckAsync);

    /// <summary>The explicit confirmed action from either UI; never called by the automatic-check loop.</summary>
    public bool Install(string instanceId) => Start(token => InstallAsync(instanceId, token));

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state.All(item => item.Stage != "Installing"))
            {
                _cancel?.Cancel();
            }
        }
    }

    internal void CloseAdmission()
    {
        lock (_gate)
        {
            _closed = true;
            if (!_disposed)
            {
                Log.Observe(_lifetime.CancelAsync(), "GPU update cancellation");
            }
        }
    }

    private bool Start(Func<CancellationToken, Task> operation)
    {
        lock (_gate)
        {
            if (_closed || !_operation.IsCompleted)
            {
                return false;
            }

            _cancel?.Dispose();
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _cancel.Token;
            _operation = Task.Run(() => operation(token));
            return true;
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        try
        {
            var adapters = GpuInstalledDriver.ReadAll();
            DiscoveryDetail = null;
            lock (_gate)
            {
                _state = [.. adapters.Select(adapter => new GpuDriverUpdateState(adapter, "Checking", "Checking the official driver catalog…"))];
            }

            Publish();
            foreach (var adapter in adapters)
            {
                try
                {
                    if (adapter.Vendor == GpuVendor.Amd && _oemRefusal() is { } refusal)
                    {
                        Set(new GpuDriverUpdateState(adapter, "Unavailable", refusal));
                        continue;
                    }

                    var package = await _catalog.CheckAsync(adapter, token).ConfigureAwait(false);
                    var available = GpuDriverUpdateCatalog.IsNewer(package);
                    var state = new GpuDriverUpdateState(adapter, available ? "Available" : "Up to date",
                        available ? $"Driver {package.Version} is available ({package.Date:d})." : "The latest compatible driver is installed.", package);
                    Set(state);
                    if (available && _announced.Add(adapter.InstanceId + "/" + package.Version))
                    {
                        _post(() => UpdateFound?.Invoke(state));
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Set(new GpuDriverUpdateState(adapter, "Failed", token.IsCancellationRequested
                        ? "Check cancelled. Check again when ready." : error.Message));
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warn("GPU driver update check: " + error.Message);
            DiscoveryDetail = "Driver discovery failed: " + error.Message + " Check again to retry.";
            lock (_gate)
            {
                _state = [.. _state.Select(item => item with { Stage = "Failed", Detail = error.Message })];
            }

            Publish();
        }
    }

    public string? InstallRefusal(GpuInstalledDriver? adapter = null)
    {
        if (ElevationCheck.IsCurrentProcessElevated() != true)
        {
            return "Restart WSGM as administrator before installing a graphics driver.";
        }

        if (adapter?.Vendor == GpuVendor.Amd && _oemRefusal() is { } oem)
        {
            return oem;
        }

        if (_gameRunning() != false)
        {
            return "Close all games before installing. Installation also waits until game detection is available.";
        }

        var known = WindowsPower.TryGetStatus(out var power);
        return PowerRefusal(known, power);
    }

    internal static string? PowerRefusal(bool known, WindowsPowerStatus power)
    {
        if (!known || power.ACLineStatus != 1)
        {
            return "Connect AC power before installing a graphics driver.";
        }

        if (power.BatteryFlag == 255)
        {
            return "Battery status is unavailable. Wait for a known power status before installing.";
        }

        if ((power.BatteryFlag & 128) == 0 && (power.BatteryLifePercent == 255 || power.BatteryLifePercent < 30))
        {
            return "Charge the battery to at least 30% before installing a graphics driver.";
        }

        return null;
    }

    private async Task InstallAsync(string instanceId, CancellationToken token)
    {
        var state = Snapshot().FirstOrDefault(item => item.Adapter.InstanceId == instanceId);
        if (state is not { CanInstall: true, Package: { } package })
        {
            return;
        }

        var dispatched = false;
        var driversPaused = false;
        string? downloaded = null;
        try
        {
            if (InstallRefusal(state.Adapter) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            Set(state with { Stage = "Downloading", Detail = "Downloading from the official vendor server…", Progress = 0 });
            var installer = new GpuDriverPackageInstaller(_catalog);
            var path = await installer.DownloadAsync(package, new DownloadProgress(percent =>
                Set(state with { Stage = "Downloading", Detail = $"Downloading… {percent}%", Progress = percent })), token)
                .ConfigureAwait(false);
            downloaded = path;
            Set(state with { Stage = "Verifying", Detail = "Verifying and preparing the vendor installer…", Progress = 100 });
            var history = new GpuDriverInstallHistory(instanceId, state.Adapter.Version, package.Version,
                Pending: true, "The installer outcome is unconfirmed. Check the installed version before another explicit attempt.");
            _store.Update(config =>
            {
                config.GpuDriverInstallHistory = history;
                return true;
            });
            driversPaused = true;
            await _driverTransition(package.Adapter.Vendor, true, token).ConfigureAwait(false);
            var result = await installer.InstallAsync(package, path, () =>
            {
                if (InstallRefusal(state.Adapter) is { } refused)
                {
                    return refused;
                }

                var current = GpuInstalledDriver.ReadAll().FirstOrDefault(item => item.InstanceId == instanceId);
                if (current?.Version != state.Adapter.Version)
                {
                    return "The adapter or installed driver changed. Check for updates again.";
                }

                dispatched = true;
                Set(state with { Stage = "Installing", Detail = "Installing the verified graphics driver. Displays may go black…" });
                return null;
            }, token).ConfigureAwait(false);
            _store.Update(config =>
            {
                config.GpuDriverInstallHistory = history with { Pending = false, Result = result.Detail };
                return true;
            });
            GpuInstalledDriver? observed = null;
            try
            {
                observed = GpuInstalledDriver.ReadAll().FirstOrDefault(adapter => adapter.InstanceId == instanceId);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warn("GPU driver identity refresh: " + error.Message);
            }

            Set(state with { Adapter = observed ?? state.Adapter,
                Stage = result.Succeeded ? result.RestartRequired ? "Restart required" : "Installed" : "Failed",
                Detail = result.Detail, Package = null, Progress = 100 });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!dispatched)
            {
                try
                {
                    _store.Update(config =>
                    {
                        if (config.GpuDriverInstallHistory is not { Pending: true } history
                            || history.Adapter != instanceId || history.NewVersion != package.Version)
                        {
                            return false;
                        }

                        config.GpuDriverInstallHistory = history with { Pending = false,
                            Result = "Preparation stopped before the installer started. " + error.Message };
                        return true;
                    });
                }
                catch (Exception historyError) when (historyError is not OutOfMemoryException)
                {
                    Log.Warn("GPU driver preparation record: " + historyError.Message);
                }
            }

            Set(state with { Stage = "Failed", Detail = dispatched
                ? "Installation outcome is uncertain. Check the installed version before another explicit attempt. " + error.Message
                : token.IsCancellationRequested ? "Download cancelled. No driver was installed." : error.Message,
                Package = dispatched ? null : state.Package });
            Log.Warn("GPU driver update: " + error.Message);
        }
        finally
        {
            if (downloaded is not null)
            {
                try
                {
                    System.IO.File.Delete(downloaded);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Log.Warn("GPU driver package cleanup: " + error.Message);
                }
            }

            if (driversPaused && !_lifetime.IsCancellationRequested)
            {
                try
                {
                    await _driverTransition(package.Adapter.Vendor, false, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Log.Warn("GPU driver update control refresh: " + error.Message);
                    Set(state with { Stage = "Failed", Package = null,
                        Detail = "The installer finished, but GPU controls could not reconnect. Restart WSGM. " + error.Message });
                }
            }
        }
    }

    private void Set(GpuDriverUpdateState state)
    {
        lock (_gate)
        {
            _state = [.. _state.Select(item => item.Adapter.InstanceId == state.Adapter.InstanceId ? state : item)];
        }

        Publish();
    }

    private void Publish() => _post(() => Changed?.Invoke());

    private sealed class DownloadProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private async Task AutomaticAsync()
    {
        try
        {
            // Always read identity once; only network checks depend on the preference.
            lock (_gate)
            {
                _state = [.. GpuInstalledDriver.ReadAll().Select(adapter => new GpuDriverUpdateState(adapter,
                    "Not checked", "Check for the latest compatible graphics driver."))];
            }

            DiscoveryDetail = null;
            Publish();
            while (!_lifetime.IsCancellationRequested)
            {
                if (Automatic)
                {
                    Check();
                }

                await Task.Delay(TimeSpan.FromHours(24), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warn("GPU driver update discovery: " + error.Message);
            DiscoveryDetail = "Driver discovery failed: " + error.Message + " Check again to retry.";
            Publish();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _closed = true;
            _disposed = true;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_automatic, _operation).ConfigureAwait(false);
        _catalog.Dispose();
        _cancel?.Dispose();
        _lifetime.Dispose();
    }
}
