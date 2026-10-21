using System;
using System.Collections.Generic;
using System.IO;
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
            var present = GpuDriver.DetectVendors();
            var desired = BuiltinGpuDrivers.All.Where(driver => present.Contains(driver.Vendor)
                && BuiltinGpuDrivers.Enabled(config, driver.Vendor)).ToArray();
            foreach (var vendor in _drivers.Keys.Where(vendor => desired.All(driver => driver.Vendor != vendor)).ToArray())
            {
                var retiring = _drivers[vendor];
                _drivers.Remove(vendor);
                await retiring.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var definition in desired.Where(definition => !_drivers.ContainsKey(definition.Vendor)))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var driver = GpuDriver.Create(definition.Vendor, Path.Combine(_store.Context.Root, "Graphics", definition.Id),
                    entry => Log.Info($"Graphics/{definition.Name}/{entry.Scope}: {entry.Message}"));
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
