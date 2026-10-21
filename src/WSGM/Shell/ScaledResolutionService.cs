using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using Windows.Devices.Display;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record ScaledResolutionDisplay(string Path, string Name)
{
    public override string ToString() => Name;
}

internal sealed record ScaledResolutionState(
    IReadOnlyList<ScaledResolutionDisplay> Displays,
    string? SelectedPath,
    GpuScaledResolutionCatalog? Catalog,
    GpuScaledResolution? Native,
    IReadOnlyList<ScaledResolutionAddition> Remembered,
    IReadOnlyList<ScaledResolutionAddition> Missing,
    string Detail)
{
    internal IReadOnlyList<GpuScaledResolution> Available { get; init; } = [];
}

/// <summary>Shared Display-settings policy over existing serialized native owners, independent of game profiles.</summary>
internal sealed class ScaledResolutionService
{
    private readonly SemaphoreSlim _edits = new(1, 1);
    private readonly ConfigStore _store;
    private readonly Func<IReadOnlyList<GpuDriver>> _drivers;
    private readonly Func<Task> _ready;

    internal ScaledResolutionService(ConfigStore store, BuiltinGpuService drivers)
        : this(store, drivers.ScaledResolutionDrivers, () => drivers.StartupCompletion)
    {
    }

    internal ScaledResolutionService(ConfigStore store, Func<IReadOnlyList<GpuDriver>> drivers, Func<Task> ready)
    {
        _store = store;
        _drivers = drivers;
        _ready = ready;
    }

    internal async Task<ScaledResolutionState> ReadAsync(string? selectedPath,
        CancellationToken token = default)
    {
        return await Task.Run(async () =>
        {
            await _ready().WaitAsync(token).ConfigureAwait(false);
            var displays = DisplayLayouts.Observe().Targets.Where(target => target.Available).ToArray();
            var choices = displays.Select(display => new ScaledResolutionDisplay(display.Target.DevicePath,
                display.Target.FriendlyName)).ToArray();
            var selected = selectedPath is null ? displays.FirstOrDefault()
                : displays.FirstOrDefault(display => string.Equals(display.Target.DevicePath, selectedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                return new ScaledResolutionState(choices, selectedPath, null, null, [], [],
                    "The selected display is disconnected. Refresh and select a connected display.");
            }

            var target = selected.Target;
            var route = new GpuDisplayTarget
            {
                DevicePath = target.DevicePath, EdidIdentity = target.EdidIdentity,
                AdapterLowPart = target.AdapterLowPart, AdapterHighPart = target.AdapterHighPart,
                TargetId = target.TargetId
            };
            GpuScaledResolutionCatalog? catalog = null;
            foreach (var driver in _drivers())
            {
                token.ThrowIfCancellationRequested();
                catalog = await driver.ReadScaledResolutionsAsync(route, token).ConfigureAwait(false);
                if (catalog is not null)
                {
                    break;
                }
            }

            GpuScaledResolution? native = null;
            try
            {
                var monitor = await DisplayMonitor.FromInterfaceIdAsync(target.DevicePath).AsTask(token)
                    .WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                if (monitor is not null)
                {
                    native = new GpuScaledResolution(monitor.NativeResolutionInRawPixels.Width,
                        monitor.NativeResolutionInRawPixels.Height);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warn("Scaled resolution native-size discovery: " + error.Message);
            }

            var remembered = _store.Read().RequireConfig().ScaledResolutions.Where(addition => addition.Valid
                && catalog is not null && Belongs(addition, catalog)).ToArray();
            var missing = catalog is { Supported: true }
                ? remembered.Where(addition => !catalog.Modes.Contains(new GpuScaledResolution(addition.Width,
                    addition.Height))).ToArray() : [];
            GpuScaledResolution[] available = [];
            try
            {
                available = DisplayModes.Read(target)?.Supported
                    .Select(mode => new GpuScaledResolution(mode.Width, mode.Height)).Distinct().ToArray() ?? [];
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log.Warn("Optional scaled-resolution Windows-mode discovery: " + error.Message);
            }
            return new ScaledResolutionState(choices, target.DevicePath, catalog, native, remembered, missing,
                catalog?.Detail ?? "No enabled GPU driver owner could resolve this display's driving adapter. Enable its built-in GPU integration in Plugins.")
            {
                Available = available
            };
        }, token).ConfigureAwait(false);
    }

    /// <summary>Explicitly refreshes native handles after hotplug or a driver update, without repeating a write.</summary>
    internal async Task RefreshTopologyAsync(CancellationToken token = default)
    {
        await _ready().WaitAsync(token).ConfigureAwait(false);
        foreach (var driver in _drivers())
        {
            await driver.RefreshTopologyAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>Preview includes native 100% down to 50%, in 5% steps of both width and height.</summary>
    internal static IReadOnlyList<GpuScaledResolution> PercentagePreview(GpuScaledResolution native)
    {
        return Enumerable.Range(0, 11).Select(index => 100 - index * 5)
            .Select(percent => new GpuScaledResolution(
                (int)Math.Round(native.Width * percent / 100d, MidpointRounding.AwayFromZero),
                (int)Math.Round(native.Height * percent / 100d, MidpointRounding.AwayFromZero)))
            .Where(mode => mode.Valid).Distinct().ToArray();
    }

    internal async Task<GpuScaledResolutionResult> AddAsync(GpuScaledResolutionCatalog observed,
        IReadOnlyList<GpuScaledResolution> modes, CancellationToken token = default)
    {
        await _edits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Strict persistence preflight occurs before the first external write, without holding its writer lock.
            _store.Read().RequireConfig();
            if (modes.Count == 0 || modes.Any(mode => !mode.Valid))
            {
                return Refused("Choose valid source sizes with dimensions from 100 to 15999 pixels.");
            }

            var fresh = await ReadAsync(observed.Display.DevicePath, token).ConfigureAwait(false);
            if (!Same(observed, fresh.Catalog))
            {
                return Refused("The display's driving adapter changed. Refresh before applying.");
            }

            var driver = await DriverAsync(observed, token).ConfigureAwait(false);
            if (driver is null)
            {
                return Refused("The driving GPU integration is no longer enabled.");
            }

            var changed = false;
            var last = new GpuScaledResolutionResult(true, false, GpuScaledResolutionActivation.Immediate,
                "These source sizes already exist; existing modes were preserved.");
            foreach (var mode in modes.Distinct())
            {
                if (fresh.Catalog!.Modes.Contains(mode)
                    || (fresh.Available.Contains(mode) || fresh.Native == mode)
                    && !fresh.Remembered.Any(item => item.Width == mode.Width && item.Height == mode.Height))
                {
                    continue;
                }

                last = await driver.SetScaledResolutionAsync(observed, mode, remove: false, token)
                    .ConfigureAwait(false);
                if (!last.Accepted)
                {
                    return last with { Changed = changed, Detail = (changed ? "Earlier sizes were saved. " : "") + last.Detail };
                }

                if (last.Changed)
                {
                    changed = true;
                    var addition = new ScaledResolutionAddition
                    {
                        DisplayPath = observed.Display.DevicePath!, EdidIdentity = observed.Display.EdidIdentity,
                        AdapterIdentity = observed.AdapterIdentity, Width = mode.Width, Height = mode.Height
                    };
                    try
                    {
                        _store.Update(config =>
                        {
                            if (!config.ScaledResolutions.Any(item => Belongs(item, observed)
                                && item.Width == addition.Width && item.Height == addition.Height))
                            {
                                config.ScaledResolutions.Add(addition);
                            }

                            return true;
                        });
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return last with { Detail = "The driver saved the source size, but WSGM could not remember it: "
                            + error.Message + ". Refresh before another explicit edit." };
                    }
                }
            }

            return last with { Changed = changed };
        }
        finally
        {
            _edits.Release();
        }
    }

    internal async Task<GpuScaledResolutionResult> RemoveAsync(GpuScaledResolutionCatalog observed,
        ScaledResolutionAddition addition, CancellationToken token = default)
    {
        await _edits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_store.Read().RequireConfig().ScaledResolutions.Contains(addition) || !Belongs(addition, observed))
            {
                return Refused("Only WSGM's remembered additions can be removed here.");
            }

            var driver = await DriverAsync(observed, token).ConfigureAwait(false);
            if (driver is null)
            {
                return Refused("The driving adapter changed or its GPU integration is unavailable.");
            }

            var result = await driver.SetScaledResolutionAsync(observed,
                new GpuScaledResolution(addition.Width, addition.Height), remove: true, token).ConfigureAwait(false);
            if (result.Accepted)
            {
                _store.Update(config => config.ScaledResolutions.RemoveAll(item => Belongs(item, observed)
                    && item.Width == addition.Width && item.Height == addition.Height) > 0);
            }

            return result;
        }
        finally
        {
            _edits.Release();
        }
    }

    internal async Task<GpuScaledResolutionResult> ReapplyAsync(GpuScaledResolutionCatalog observed,
        CancellationToken token = default)
    {
        var state = await ReadAsync(observed.Display.DevicePath, token).ConfigureAwait(false);
        if (!Same(observed, state.Catalog))
        {
            return Refused("The driving adapter changed. Refresh before reapplying.");
        }

        return state.Missing.Count == 0 ? Refused("No remembered additions are missing.")
            : await AddAsync(observed, state.Missing.Select(item => new GpuScaledResolution(item.Width, item.Height))
                .ToArray(), token).ConfigureAwait(false);
    }

    internal async Task<GpuScaledResolutionResult> RestartAsync(GpuScaledResolutionCatalog observed,
        CancellationToken token = default)
    {
        await _edits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var driver = await DriverAsync(observed, token).ConfigureAwait(false);
            if (driver is null)
            {
                return Refused("The driving adapter changed. Refresh before restarting.");
            }

            var result = await driver.RestartScaledResolutionDriverAsync(observed, token).ConfigureAwait(false);
            if (result.Accepted)
            {
                await RefreshTopologyAsync(token).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _edits.Release();
        }
    }

    private async Task<GpuDriver?> DriverAsync(GpuScaledResolutionCatalog observed, CancellationToken token)
    {
        foreach (var driver in _drivers())
        {
            if (Same(observed, await driver.ReadScaledResolutionsAsync(observed.Display, token).ConfigureAwait(false)))
            {
                return driver;
            }
        }

        return null;
    }

    internal static bool Same(GpuScaledResolutionCatalog observed, GpuScaledResolutionCatalog? current)
    {
        return current is { Supported: true } && current.Vendor == observed.Vendor
            && current.AdapterIdentity == observed.AdapterIdentity && current.Display == observed.Display;
    }

    private static bool Belongs(ScaledResolutionAddition addition, GpuScaledResolutionCatalog catalog)
    {
        return addition.AdapterIdentity == catalog.AdapterIdentity && (!catalog.OutputSpecific
            || string.Equals(addition.DisplayPath, catalog.Display.DevicePath, StringComparison.OrdinalIgnoreCase)
            && addition.EdidIdentity == catalog.Display.EdidIdentity);
    }

    private static GpuScaledResolutionResult Refused(string detail)
    {
        return new GpuScaledResolutionResult(false, false, GpuScaledResolutionActivation.Immediate, detail);
    }
}
