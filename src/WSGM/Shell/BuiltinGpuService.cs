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
    private readonly GpuCoordinator _coordinator;
    private readonly Dictionary<GpuVendor, Entry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _registration = new();
    private readonly ConfigStore _store;
    private volatile bool _closed;
    private GpuVendor[] _configured = [];
    private bool _suspended;

    internal BuiltinGpuService(ConfigStore store, GpuCoordinator coordinator)
    {
        _store = store;
        _coordinator = coordinator;
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

            var installed = await Task.Run(GpuDriver.DetectVendors, linked.Token).ConfigureAwait(false);
            var desired = configured.Where(installed.Contains).ToArray();
            Log.Change("graphics.activation",
                $"Graphics drivers: detected=[{string.Join(',', installed)}], configured=[{string.Join(',', configured)}], admitted=[{string.Join(',', desired)}].");
            Entry[] retiring;
            lock (_registration)
            {
                retiring = _entries.Where(pair => !desired.Contains(pair.Key) || pair.Value.Retirement is not null)
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
                    entry = new Entry(definition, new GpuDriverPublisher(_coordinator, definition, directory));
                    _entries.Add(vendor, entry);
                }

                starts.Add(StartAsync(entry, linked.Token));
            }

            await Task.WhenAll(starts).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task StartAsync(Entry entry, CancellationToken token)
    {
        using var wait = Deadline.After(TimeSpan.FromSeconds(5)).CreateCancellationSource(token);
        try
        {
            await entry.Publisher.StartAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            // Cancellation bounds this wait. The driver still owns initialization and late publication.
            Log.Info($"Graphics {entry.Definition.Name} startup is continuing in its native owner.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warn($"Graphics {entry.Definition.Name} startup failed: {error.Message}");
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
            Log.Warn("Graphics cleanup is still completing an owned native call.");
        }
    }

    private static Task Retire(Entry entry)
    {
        lock (entry)
        {
            entry.Publisher.CloseAdmission();
            return entry.Retirement ??= entry.Publisher.DisposeAsync().AsTask();
        }
    }

    private sealed class Entry(BuiltinGpuDriver definition, GpuDriverPublisher publisher)
    {
        internal Task? Retirement;
        internal BuiltinGpuDriver Definition { get; } = definition;
        internal GpuDriverPublisher Publisher { get; } = publisher;
    }
}
