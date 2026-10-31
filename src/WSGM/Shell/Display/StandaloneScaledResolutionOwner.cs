using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Standalone Settings' native driver lifetime; resident Settings borrows its session's owners instead.</summary>
internal sealed class StandaloneScaledResolutionOwner
{
    private readonly ConfigStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<GpuVendor, GpuDriver> _drivers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private GpuDriver[] _snapshot = [];
    private Task? _shutdown;

    internal StandaloneScaledResolutionOwner(ConfigStore store)
    {
        _store = store;
        Service = new ScaledResolutionService(store, () => Volatile.Read(ref _snapshot), ReconcileAsync);
    }

    internal ScaledResolutionService Service { get; }

    private async Task ReconcileAsync()
    {
        await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var config = _store.Read().RequireConfig();
            var present = await Task.Run(() => BuiltinGpuDrivers.Detect(), _lifetime.Token).ConfigureAwait(false);
            var desired = present.Where(driver => BuiltinGpuDrivers.Enabled(config, driver.Vendor)).ToArray();
            foreach (var vendor in _drivers.Keys.Where(vendor => desired.All(driver => driver.Vendor != vendor)).ToArray())
            {
                var retiring = _drivers[vendor];
                _drivers.Remove(vendor);
                await retiring.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var definition in desired.Where(definition => !_drivers.ContainsKey(definition.Vendor)))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                // The session's state folder: the library's persistent state belongs to the driver,
                // not to whichever owner started it.
                var driver = BuiltinGpuDrivers.Create(definition, BuiltinGpuDrivers.StateDirectory(_store.Context, definition));
                _drivers.Add(definition.Vendor, driver);
                await driver.StartAsync(_lifetime.Token).ConfigureAwait(false);
            }

            Volatile.Write(ref _snapshot, _drivers.Values.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask ShutdownAsync(ApplicationShutdownReason reason, DateTimeOffset deadline)
    {
        _lifetime.Cancel();
        Volatile.Write(ref _snapshot, []);
        var shutdown = _shutdown ??= StopOwnedAsync();
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await shutdown.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("Standalone graphics cleanup is still completing an owned native call.");
        }
    }

    private async Task StopOwnedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_drivers.Values.Select(driver => driver.DisposeAsync().AsTask())).ConfigureAwait(false);
            _drivers.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
