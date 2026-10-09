using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Install;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Owns the directly linked GPU drivers independently of common-plugin and device lifetimes.</summary>
internal sealed class BuiltinGpuService : IAsyncDisposable
{
    private readonly GpuCoordinator _coordinator;
    private readonly ConcurrentDictionary<PluginInstanceIdentity, Entry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _registration = new();
    private readonly ConfigStore _store;
    private volatile bool _closed;
    private CommonPluginInstanceConfig[] _configured = [];
    private bool _suspended;

    internal BuiltinGpuService(ConfigStore store, GpuCoordinator coordinator)
    {
        _store = store;
        _coordinator = coordinator;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(Deadline.After(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);
    }

    internal Task ReconcileAsync(AppConfig config, CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        var configured = config.PluginInstances.Where(instance => BuiltinGpuDrivers.Contains(instance.PluginId))
            .Select(instance => new CommonPluginInstanceConfig
            {
                PluginId = instance.PluginId, InstanceId = instance.InstanceId, Enabled = instance.Enabled
            }).ToArray();
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

            CommonPluginInstanceConfig[] configured;
            lock (_registration)
            {
                configured = _configured;
            }

            var adapters = await Task.Run(CommonPluginEnablement.ReadAdapters, linked.Token).ConfigureAwait(false);
            var desired = BuiltinGpuDrivers.Instances(configured)
                .Where(instance => instance.Enabled && DisplayAdapterInventory.AnyVendor(adapters,
                    [instance.Driver.PciVendorId])).ToArray();
            foreach (var entry in _entries.Values.Where(entry =>
                         desired.All(instance => instance.Identity != entry.Identity)))
            {
                Retire(entry);
            }

            if (_suspended)
            {
                return;
            }

            foreach (var instance in desired)
            {
                if (_entries.TryGetValue(instance.Identity, out var existing))
                {
                    lock (existing)
                    {
                        if (existing.Retirement is null)
                        {
                            continue;
                        }

                        if (!existing.Retirement.IsCompleted)
                        {
                            // A later user/configuration request may restart after this owner retires.
                            existing.RestartRequested = true;
                            continue;
                        }

                        if (!existing.Clean)
                        {
                            continue;
                        }
                    }

                    _entries.TryRemove(instance.Identity, out _);
                }

                linked.Token.ThrowIfCancellationRequested();
                var directory = Path.Combine(_store.Context.Root, "PluginState", instance.Identity.PluginId,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance.Identity.InstanceId))));
                Entry entry;
                lock (_registration)
                {
                    if (_closed)
                    {
                        return;
                    }

                    var driver = new GpuDriverAdapter(_coordinator, instance.Driver, instance.Identity, directory);
                    entry = new Entry(instance.Identity, driver);
                    if (!_entries.TryAdd(instance.Identity, entry))
                    {
                        throw new InvalidOperationException("A graphics driver identity was registered twice.");
                    }
                }

                try
                {
                    using var start = Deadline.After(TimeSpan.FromSeconds(5)).CreateCancellationSource(linked.Token);
                    await entry.Adapter.StartAsync(start.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Log.Warn($"Graphics {instance.Driver.Name} startup failed: {error.Message}");
                    Retire(entry);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task PowerTransitionAsync(bool suspend, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_closed)
            {
                return;
            }

            _suspended = suspend;
            foreach (var entry in _entries.Values.Where(entry => entry.Retirement is null))
            {
                try
                {
                    using var budget = Deadline.After(TimeSpan.FromSeconds(5)).CreateCancellationSource(linked.Token);
                    if (suspend)
                    {
                        await entry.Adapter.SuspendAsync(budget.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await entry.Adapter.ResumeAsync(budget.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Log.Warn($"Graphics {entry.Identity.PluginId} power transition failed: {error.Message}");
                }
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

    internal void CloseAdmission()
    {
        lock (_registration)
        {
            _closed = true;
            _lifetime.CancelAsync().ObserveFaults();
            foreach (var entry in _entries.Values)
            {
                entry.Adapter.CloseAdmission();
            }
        }
    }

    internal async Task StopAsync(Deadline deadline)
    {
        CloseAdmission();
        Entry[] entries;
        lock (_registration)
        {
            entries = [.. _entries.Values];
        }

        foreach (var entry in entries)
        {
            Retire(entry);
        }

        using var budget = deadline.CreateCancellationSource();
        try
        {
            await Task.WhenAll(entries.Select(entry => entry.Retirement!)).WaitAsync(budget.Token)
                .ConfigureAwait(false);
            if (entries.Any(entry => !entry.Clean))
            {
                Log.Warn("A graphics driver did not confirm clean retirement; its ownership remains retained.");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Graphics driver cleanup is still completing an owned native call.");
        }
    }

    private void Retire(Entry entry)
    {
        lock (entry)
        {
            if (entry.Retirement is not null)
            {
                return;
            }

            entry.Adapter.CloseAdmission();
            entry.Retirement = Task.Run(async () =>
            {
                try
                {
                    if (!await entry.Adapter.StopAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        Log.Warn($"Graphics {entry.Identity.PluginId} retirement remains unconfirmed.");
                        return;
                    }

                    await entry.Adapter.DisposeAsync().ConfigureAwait(false);
                    entry.Clean = true;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Log.Warn($"Graphics {entry.Identity.PluginId} cleanup failed: {error.Message}");
                }
            });
            entry.Retirement.ContinueWith(_ =>
            {
                bool restart;
                lock (entry)
                {
                    restart = entry.Clean && entry.RestartRequested;
                }

                if (!_closed && restart)
                {
                    Log.Observe(ReconcileCurrentAsync(_lifetime.Token), "Graphics retirement reconciliation", true);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed class Entry(PluginInstanceIdentity identity, GpuDriverAdapter adapter)
    {
        internal bool Clean;
        internal bool RestartRequested;
        internal Task? Retirement;
        internal PluginInstanceIdentity Identity { get; } = identity;
        internal GpuDriverAdapter Adapter { get; } = adapter;
    }
}
