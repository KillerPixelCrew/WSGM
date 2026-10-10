using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>Owns one native driver per enabled installed vendor, independently of common plugins.</summary>
internal sealed class BuiltinGpuService : IAsyncDisposable
{
    private readonly Func<BuiltinGpuDriver, string, IBuiltinGpuDriver> _createDriver;
    private readonly Func<IReadOnlyList<GpuVendor>> _detectVendors;
    private readonly Dictionary<GpuVendor, Entry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _registration = new();
    private readonly Action<string, bool> _report;
    private readonly TimeSpan _startupWait;
    private readonly ConfigStore _store;
    private string? _activation;
    private volatile bool _closed;
    private GpuVendor[] _configured = [];
    private bool _suspended;

    internal BuiltinGpuService(ConfigStore store, GpuCoordinator coordinator)
        : this(store, GpuDriver.DetectVendors,
            (definition, directory) => new GpuDriverPublisher(coordinator, definition, directory),
            TimeSpan.FromSeconds(5), (message, warning) =>
            {
                if (warning)
                {
                    Log.Warn(message);
                }
                else
                {
                    Log.Info(message);
                }
            })
    {
    }

    internal BuiltinGpuService(ConfigStore store, Func<IReadOnlyList<GpuVendor>> detectVendors,
        Func<BuiltinGpuDriver, string, IBuiltinGpuDriver> createDriver, TimeSpan startupWait,
        Action<string, bool> report)
    {
        _store = store;
        _detectVendors = detectVendors;
        _createDriver = createDriver;
        _startupWait = startupWait;
        _report = report;
    }

    /// <summary>The registered startup work, including initialization whose caller has stopped waiting.</summary>
    internal Task StartupCompletion
    {
        get
        {
            lock (_registration)
            {
                return Task.WhenAll(_entries.Values.Select(entry => entry.Startup));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(Deadline.After(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    internal Task ReconcileAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var configured = BuiltinGpuDrivers.All.Where(driver => BuiltinGpuDrivers.Enabled(config, driver.Vendor))
            .Select(driver => driver.Vendor).ToArray();
        lock (_registration)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            _configured = configured;
        }

        return ReconcileCurrentAsync(cancellationToken);
    }

    private async Task ReconcileCurrentAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_closed)
            {
                return;
            }

            GpuVendor[] configured;
            lock (_registration)
            {
                configured = _configured;
            }

            var installed = await Task.Run(_detectVendors, linked.Token).ConfigureAwait(false);
            var desired = configured.Where(installed.Contains).ToArray();
            ReportActivation(installed, configured, desired);
            Entry[] retiring;
            lock (_registration)
            {
                retiring = _entries.Where(pair => !desired.Contains(pair.Key) || pair.Value.Retirement is not null
                                                                              || pair.Value.StartupFailed)
                    .Select(pair => pair.Value).ToArray();
            }

            foreach (var entry in retiring)
            {
                await Retire(entry).WaitAsync(linked.Token).ConfigureAwait(false);
                lock (_registration)
                {
                    _entries.Remove(entry.Definition.Vendor);
                }
            }

            if (_suspended)
            {
                return;
            }

            var starts = new List<Task>();
            foreach (var vendor in desired)
            {
                Entry entry;
                lock (_registration)
                {
                    if (_closed)
                    {
                        return;
                    }

                    if (_entries.ContainsKey(vendor))
                    {
                        continue;
                    }

                    var definition = BuiltinGpuDrivers.All.First(driver => driver.Vendor == vendor);
                    var directory = Path.Combine(_store.Context.Root, "GpuState", definition.Id);
                    entry = new Entry(definition, _createDriver(definition, directory));
                    _entries.Add(vendor, entry);
                    // Register the actual owned startup before shutdown can see the entry. Only the
                    // caller's wait expires; native startup keeps its independent lifetime and is joined.
                    entry.Startup = ObserveStartupAsync(entry);
                }

                starts.Add(WaitForStartupAsync(entry, linked.Token));
            }

            await Task.WhenAll(starts).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReportActivation(IReadOnlyList<GpuVendor> installed, GpuVendor[] configured, GpuVendor[] desired)
    {
        var text =
            $"Graphics drivers: detected=[{string.Join(',', installed)}], configured=[{string.Join(',', configured)}], admitted=[{string.Join(',', desired)}].";
        if (_activation == text)
        {
            return;
        }

        _activation = text;
        _report(text, false);
    }

    private async Task ObserveStartupAsync(Entry entry)
    {
        try
        {
            var health = await entry.Publisher.StartAsync(CancellationToken.None).ConfigureAwait(false);
            if (health is not GpuHealth.Ready)
            {
                MarkStartupFailed(entry, $"Graphics {entry.Definition.Name} startup finished {health}.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MarkStartupFailed(entry, $"Graphics {entry.Definition.Name} startup failed: {error.Message}");
        }
    }

    private void MarkStartupFailed(Entry entry, string message)
    {
        lock (_registration)
        {
            entry.StartupFailed = true;
            entry.Publisher.CloseAdmission();
        }

        _report(message, true);
    }

    private async Task WaitForStartupAsync(Entry entry, CancellationToken token)
    {
        try
        {
            await entry.Startup.WaitAsync(_startupWait, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _report($"Graphics {entry.Definition.Name} startup is continuing in its native owner.", false);
        }
    }

    internal async Task PowerTransitionAsync(bool suspend, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed)
            {
                return;
            }

            _suspended = suspend;
            Entry[] entries;
            lock (_registration)
            {
                entries = _entries.Values.Where(entry => entry.Retirement is null).ToArray();
            }

            foreach (var entry in entries)
            {
                entry.Publisher.SetSuspended(suspend);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (!suspend && !_closed)
        {
            await ReconcileCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task RefreshTopologyAsync(AppConfig config, CancellationToken cancellationToken)
    {
        await ReconcileAsync(config, cancellationToken).ConfigureAwait(false);
        Entry[] entries;
        lock (_registration)
        {
            entries = _entries.Values.Where(entry => entry.Retirement is null).ToArray();
        }

        await Task.WhenAll(entries.Select(entry => entry.Publisher.RefreshTopologyAsync(cancellationToken).AsTask()))
            .ConfigureAwait(false);
    }

    internal void CloseAdmission()
    {
        lock (_registration)
        {
            _closed = true;
            _lifetime.CancelAsync().ObserveFaults();
            foreach (var entry in _entries.Values)
            {
                entry.Publisher.CloseAdmission();
            }
        }
    }

    internal async Task StopAsync(Deadline deadline)
    {
        CloseAdmission();
        Entry[] entries;
        lock (_registration)
        {
            entries = _entries.Values.ToArray();
        }

        var retirement = Task.WhenAll(entries.Select(Retire));
        using var wait = deadline.CreateCancellationSource();
        try
        {
            await retirement.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _report("Graphics cleanup is still completing an owned native call.", true);
        }
    }

    private static Task Retire(Entry entry)
    {
        lock (entry)
        {
            entry.Publisher.CloseAdmission();
            return entry.Retirement ??= RetireOwnedAsync(entry);
        }
    }

    private static async Task RetireOwnedAsync(Entry entry)
    {
        await entry.Startup.ConfigureAwait(false);
        await entry.Publisher.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Entry(BuiltinGpuDriver definition, IBuiltinGpuDriver publisher)
    {
        internal Task? Retirement;
        internal Task Startup = Task.CompletedTask;
        internal bool StartupFailed;
        internal BuiltinGpuDriver Definition { get; } = definition;
        internal IBuiltinGpuDriver Publisher { get; } = publisher;
    }
}

/// <summary>The native owner's lifecycle boundary; admission closes before any owned call is joined.</summary>
internal interface IBuiltinGpuDriver : IAsyncDisposable
{
    ValueTask<GpuHealth> StartAsync(CancellationToken token);
    ValueTask RefreshTopologyAsync(CancellationToken token);
    void CloseAdmission();
    void SetSuspended(bool suspended);
}
