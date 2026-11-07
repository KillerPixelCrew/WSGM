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
    private readonly Func<GpuInstalledDriver, string?> _oemRefusal;
    private readonly Func<IReadOnlyList<GpuInstalledDriver>> _readInstalled;
    private readonly Func<GpuInstalledDriver, bool, CancellationToken, Task<GpuDriverPackage>> _checkPackage;
    private readonly Func<CancellationToken, Task> _waitForSession;
    private static readonly Lazy<bool?> ProcessElevation = new(ElevationCheck.IsCurrentProcessElevated);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private bool _hasDiscovery;
    private bool _channelCheckPending;
    private bool _running;
    private bool _loggedStart;
    private string? _discoveryDetail = "Reading installed graphics drivers…";
    private string? _lastAnnounced;
    private DateTimeOffset _admissionAt;
    private string? _admission;
    private readonly Action<Action> _post;
    private readonly Func<GpuVendor, bool, CancellationToken, Task> _driverTransition;
    private readonly GpuDriverUpdateCatalog _catalog = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private IReadOnlyList<GpuDriverUpdateState> _state = [];
    private Task _operation = Task.CompletedTask;
    private readonly Task _automatic;
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cancel;
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private bool _closed;
    private bool _disposed;

    internal GpuDriverUpdateService(ConfigStore store, Func<bool?> gameRunning, Func<GpuInstalledDriver, string?> oemRefusal,
        Action<Action> post, Func<GpuVendor, bool, CancellationToken, Task>? driverTransition = null,
        Func<IReadOnlyList<GpuInstalledDriver>>? readInstalled = null,
        Func<GpuInstalledDriver, bool, CancellationToken, Task<GpuDriverPackage>>? checkPackage = null,
        Func<CancellationToken, Task>? waitForSession = null)
    {
        _store = store;
        _gameRunning = gameRunning;
        _oemRefusal = oemRefusal;
        _post = post;
        _readInstalled = readInstalled ?? GpuInstalledDriver.ReadAll;
        _checkPackage = checkPackage ?? _catalog.CheckAsync;
        _waitForSession = waitForSession ?? (token => Task.Delay(TimeSpan.FromSeconds(10), token));
        _driverTransition = driverTransition ?? ((_, _, _) => Task.CompletedTask);
        _automatic = Task.Run(AutomaticAsync);
    }

    public event Action? Changed;
    internal event Action<GpuDriverUpdateState>? UpdateFound;
    public event Action? ProgressChanged;
    public bool IncludeNonWhql => _store.Read().Config?.GpuDriverUpdatesIncludeNonWhql == true;
    public bool Automatic => _store.Read().Config?.GpuDriverUpdatesAutoCheck == true;
    public GpuDriverInstallHistory? History => _store.Read().Config?.GpuDriverInstallHistory;
    internal Task StartupCompletion => _startup.Task;
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _operation;
            }
        }
    }
    public string? DiscoveryDetail
    {
        get
        {
            lock (_gate)
            {
                return _discoveryDetail;
            }
        }
    }
    public string? TakeAnnouncement()
    {
        lock (_gate)
        {
            var text = _lastAnnounced;
            _lastAnnounced = null;
            return text;
        }
    }
    internal void AnnounceFallback(GpuDriverUpdateState update)
    {
        lock (_gate)
        {
            _lastAnnounced = $"{update.Adapter.Name}: driver {update.Package!.Version} is available. "
                + "Open Device > GPU > Driver updates to review and install it.";
        }
        Publish();
    }

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
        if (enabled && CheckDue())
        {
            Check();
        }
    }

    public void SetIncludeNonWhql(bool enabled)
    {
        lock (_gate)
        {
            _store.Update(config =>
            {
                config.GpuDriverUpdatesIncludeNonWhql = enabled;
                return true;
            });
            _channelCheckPending = true;
            if (!_running)
            {
                _channelCheckPending = false;
                Check();
            }
        }
        Publish();
    }

    /// <summary>Starts a bounded check and returns immediately so neither surface holds a bridge command open.</summary>
    public bool Check() => Start(CheckAsync, discovers: true);

    /// <summary>The explicit confirmed action from either UI; never called by the automatic-check loop.</summary>
    public bool Install(string instanceId, string confirmedPackageIdentity)
    {
        lock (_gate)
        {
            if (_state.FirstOrDefault(item => item.Adapter.InstanceId == instanceId) is not
                { CanInstall: true, Package: { } package } || package.Identity != confirmedPackageIdentity
                || (!IncludeNonWhql && !package.IsWhql))
            {
                return false;
            }

            return Start(token => InstallAsync(instanceId, confirmedPackageIdentity, token));
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state.Any(item => item.CanCancel))
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

    private bool Start(Func<CancellationToken, Task> operation, bool discovers = false)
    {
        lock (_gate)
        {
            if (_closed || _running)
            {
                return false;
            }

            _running = true;
            _hasDiscovery |= discovers;
            _cancel?.Dispose();
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _cancel.Token;
            _operation = Task.Run(async () =>
            {
                while (true)
                {
                    await operation(token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!_channelCheckPending || _closed)
                        {
                            _running = false;
                            return;
                        }
                        _channelCheckPending = false;
                        _cancel?.Dispose();
                        _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        token = _cancel.Token;
                        operation = CheckAsync;
                    }
                }
            });
            return true;
        }
    }

    private async Task DiscoverAsync(CancellationToken token)
    {
        try
        {
            var adapters = ReadInstalledForCheck();
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _discoveryDetail = null;
                _state = [.. adapters.Select(adapter => new GpuDriverUpdateState(adapter,
                    GpuDriverUpdateStage.NotChecked, "Check for the latest compatible graphics driver."))];
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DiscoveryFailed(error);
        }

        Publish();
        await Task.CompletedTask;
    }

    private async Task CheckAsync(CancellationToken token)
    {
        var includeNonWhql = IncludeNonWhql;
        Log.Info($"GPU driver catalog check: includeNonWhql={includeNonWhql}.");
        try
        {
            var adapters = ReadInstalledForCheck();
            lock (_gate)
            {
                _discoveryDetail = null;
                _state = [.. adapters.Select(adapter => new GpuDriverUpdateState(adapter,
                    GpuDriverUpdateStage.Checking, "Checking the official driver catalog…"))];
            }

            _store.Update(config =>
            {
                config.GpuDriverUpdatesLastCheckedAt = DateTimeOffset.UtcNow;
                return true;
            });
            Publish();
            await Task.WhenAll(adapters.Select(CheckAdapterAsync)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DiscoveryFailed(error);
            Publish();
        }

        async Task CheckAdapterAsync(GpuInstalledDriver adapter)
        {
            try
            {
                if (_oemRefusal(adapter) is { } refusal)
                {
                    Set(new GpuDriverUpdateState(adapter, GpuDriverUpdateStage.Unavailable, refusal));
                    return;
                }

                var package = await _checkPackage(adapter, includeNonWhql, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (IncludeNonWhql != includeNonWhql)
                {
                    return;
                }
                Log.Info($"GPU driver catalog: {adapter.Name} [{adapter.HardwareId}], installed={adapter.Version}, offered={package.Version}, {package.Certification}.");
                var available = GpuDriverUpdateCatalog.IsNewer(package);
                var state = new GpuDriverUpdateState(adapter, available is true ? GpuDriverUpdateStage.Available
                    : available is false ? GpuDriverUpdateStage.UpToDate : GpuDriverUpdateStage.Unavailable,
                    available is true ? $"Driver {package.Version} is available ({package.Date:d})." : available is false
                        ? "The latest compatible driver in the selected channel is installed."
                        : "The installed driver date is unavailable; this release has multiple architecture-specific versions.", package);
                if (package.ChannelDetail is { } channelDetail)
                {
                    state = state with { Detail = state.Detail + " " + channelDetail };
                }
                Set(state);
                bool announce;
                lock (_gate)
                {
                    announce = available is true && _announced.Add(package.Identity);
                }

                if (announce)
                {
                    _post(() => UpdateFound?.Invoke(state));
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warn($"GPU driver catalog: {adapter.Name}: {error.Message}");
                Set(new GpuDriverUpdateState(adapter, error is NotSupportedException
                    ? GpuDriverUpdateStage.Unavailable : GpuDriverUpdateStage.Failed,
                    token.IsCancellationRequested ? "Check cancelled. Check again when ready." : error.Message));
            }
        }
    }

    private void DiscoveryFailed(Exception error)
    {
        Log.Warn("GPU driver discovery: " + error.Message);
        lock (_gate)
        {
            _discoveryDetail = "Driver discovery failed: " + error.Message + " Check again to retry.";
            _state = [.. _state.Select(item => item with { Stage = GpuDriverUpdateStage.Failed,
                Detail = error.Message, Package = null })];
        }
    }

    private IReadOnlyList<GpuInstalledDriver> ReadInstalledForCheck()
    {
        var adapters = _readInstalled();
        if (!_loggedStart)
        {
            _loggedStart = true;
            Log.Info("GPU driver update service started: adapters=["
                + string.Join(", ", adapters.Select(adapter => $"{adapter.Name} [{adapter.HardwareId}] {adapter.Version}")) + "].");
        }
        return adapters;
    }

    public string? InstallRefusal(GpuInstalledDriver adapter) => AdmissionRefusal(adapter, fresh: false);

    private string? AdmissionRefusal(GpuInstalledDriver adapter, bool fresh)
    {
        if (_oemRefusal(adapter) is { } oem)
        {
            return oem;
        }

        lock (_gate)
        {
            if (!fresh && DateTimeOffset.UtcNow - _admissionAt < TimeSpan.FromSeconds(2))
            {
                return _admission;
            }

            var known = WindowsPower.TryGetStatus(out var power);
            _admission = ProcessElevation.Value != true ? "Restart WSGM as administrator before installing a graphics driver."
                : _gameRunning() != false ? "Close all games before installing. Installation also waits until game detection is available."
                : PowerRefusal(known, power);
            _admissionAt = DateTimeOffset.UtcNow;
            return _admission;
        }
    }

    internal static string? PowerRefusal(bool known, WindowsPowerStatus power)
    {
        if (!known || PowerSource.OnAc(power) != true)
        {
            return "Connect AC power before installing a graphics driver.";
        }

        if (power.BatteryFlag == 255)
        {
            return "Battery status is unavailable. Wait for a known power status before installing.";
        }

        var (hasBattery, percent, _) = SystemStatus.InterpretBattery(known, power.BatteryFlag, power.BatteryLifePercent);
        if (power.BatteryFlag != 128 && (!hasBattery || percent < 30))
        {
            return "Charge the battery to at least 30% before installing a graphics driver.";
        }

        return null;
    }

    private async Task InstallAsync(string instanceId, string confirmedPackageIdentity, CancellationToken token)
    {
        var state = Snapshot().FirstOrDefault(item => item.Adapter.InstanceId == instanceId);
        if (state is not { CanInstall: true, Package: { } package } || package.Identity != confirmedPackageIdentity
                || (!IncludeNonWhql && !package.IsWhql))
        {
            return;
        }

        var dispatched = false;
        var driversPaused = false;
        GpuDriverDownload? downloaded = null;
        try
        {
            if (AdmissionRefusal(state.Adapter, fresh: true) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            Set(state with { Stage = GpuDriverUpdateStage.Downloading, Detail = "Downloading from the official vendor server…", Progress = 0 });
            var installer = new GpuDriverPackageInstaller(_catalog, System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSGM", "DriverUpdates"));
            var path = await installer.DownloadAsync(package, new DownloadProgress(percent =>
                Set(state with { Stage = GpuDriverUpdateStage.Downloading, Detail = "Downloading from the official vendor server…", Progress = percent })), token)
                .ConfigureAwait(false);
            downloaded = path;
            Set(state with { Stage = GpuDriverUpdateStage.Verifying, Detail = "Verifying and preparing the vendor installer…", Progress = 100 });
            var history = new GpuDriverInstallHistory(instanceId, state.Adapter.Version, package.Version,
                Pending: true, "The installer outcome is unconfirmed. Check the installed version before another explicit attempt.");
            _store.Update(config =>
            {
                config.GpuDriverInstallHistory = history;
                return true;
            });
            driversPaused = true;
            await _driverTransition(package.Adapter.Vendor, true, token).ConfigureAwait(false);
            var result = await installer.InstallAsync(path, () =>
            {
                if (!IncludeNonWhql && !package.IsWhql)
                {
                    return "The non-WHQL channel was disabled. Check for updates again.";
                }

                if (AdmissionRefusal(state.Adapter, fresh: true) is { } refused)
                {
                    return refused;
                }

                var current = _readInstalled().FirstOrDefault(item => item.InstanceId == instanceId);
                if (current?.Version != state.Adapter.Version)
                {
                    return "The adapter or installed driver changed. Check for updates again.";
                }

                dispatched = true;
                Set(state with { Stage = GpuDriverUpdateStage.Installing, Detail = "Installing the verified graphics driver. Displays may go black…" });
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
                observed = _readInstalled().FirstOrDefault(adapter => adapter.InstanceId == instanceId);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warn("GPU driver identity refresh: " + error.Message);
            }

            Set(state with { Adapter = observed ?? state.Adapter,
                Stage = result.Succeeded ? result.RestartRequired ? GpuDriverUpdateStage.RestartRequired : GpuDriverUpdateStage.Installed : GpuDriverUpdateStage.Failed,
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

            Set(state with { Stage = GpuDriverUpdateStage.Failed, Detail = dispatched
                ? "Installation outcome is uncertain. Check the installed version before another explicit attempt. " + error.Message
                : token.IsCancellationRequested ? "Download cancelled. No driver was installed." : error.Message,
                Package = dispatched ? null : state.Package });
            Log.Warn("GPU driver update: " + error.Message);
        }
        finally
        {
            if (downloaded is not null)
            {
                await downloaded.DisposeAsync().ConfigureAwait(false);
                FileCleanup.TryDelete(downloaded.Path, error => Log.Warn("GPU driver package cleanup: " + error.Message));
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
                    Set(state with { Stage = GpuDriverUpdateStage.Failed, Package = null,
                        Detail = "The installer finished, but GPU controls could not reconnect. Restart WSGM. " + error.Message });
                }
            }
        }
    }

    private void Set(GpuDriverUpdateState state)
    {
        bool progressOnly;
        lock (_gate)
        {
            var previous = _state.FirstOrDefault(item => item.Adapter.InstanceId == state.Adapter.InstanceId);
            progressOnly = previous is not null && state with { Progress = previous.Progress } == previous;
            _state = [.. _state.Select(item => item.Adapter.InstanceId == state.Adapter.InstanceId ? state : item)];
        }

        if (progressOnly)
        {
            _post(() => ProgressChanged?.Invoke());
        }
        else
        {
            Publish();
        }
    }

    private void Publish() => _post(() => Changed?.Invoke());

    private sealed class DownloadProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private bool CheckDue()
    {
        return _store.Read().Config?.GpuDriverUpdatesLastCheckedAt is not { } checkedAt
            || DateTimeOffset.UtcNow - checkedAt >= CheckInterval;
    }

    private async Task AutomaticAsync()
    {
        try
        {
            await _waitForSession(_lifetime.Token).ConfigureAwait(false);
            while (!_lifetime.IsCancellationRequested)
            {
                if (Automatic && CheckDue())
                {
                    Check();
                }
                else
                {
                    lock (_gate)
                    {
                        if (!_hasDiscovery)
                        {
                            Start(DiscoverAsync, discovers: true);
                        }
                    }
                }

                var delay = Automatic && _store.Read().Config?.GpuDriverUpdatesLastCheckedAt is { } last
                    ? CheckInterval - (DateTimeOffset.UtcNow - last) : CheckInterval;
                _startup.TrySetResult();
                await Task.Delay(delay > TimeSpan.FromMinutes(1) ? delay : TimeSpan.FromMinutes(1), _lifetime.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DiscoveryFailed(error);
            Publish();
        }
        finally
        {
            _startup.TrySetResult();
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
