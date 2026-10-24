using System;
using System.Collections.Generic;
using System.Globalization;
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
    internal ScaledResolutionBackup? Original { get; init; }

    /// <summary>Whether edits update an adapter-wide list activated by restarting Windows or its driver.</summary>
    internal bool RequiresRestart => Catalog is { Supported: true, OutputSpecific: false };

    /// <summary>Activation guidance shown before editing on both settings surfaces.</summary>
    internal string ActivationNotice => RequiresRestart
        ? "Restart Windows after changing this resolution list. AMD and NVIDIA changes are not available until Windows or the graphics driver is restarted."
        : "";

    internal IReadOnlyList<GpuScaledResolution> Resolutions => (Catalog?.Modes ?? [])
        .Distinct().OrderByDescending(mode => mode.Width).ThenByDescending(mode => mode.Height).ToArray();

    internal string ResolutionLabel(GpuScaledResolution mode) => $"{mode.Width} × {mode.Height}"
        + (mode == Native ? " · native" : "");
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
            var choices = displays.Select((display, index) => new ScaledResolutionDisplay(display.Target.DevicePath,
                string.IsNullOrWhiteSpace(display.Target.FriendlyName)
                    ? $"Display {index + 1}"
                    : display.Target.FriendlyName)).ToArray();
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

            var config = _store.Read().RequireConfig();
            var remembered = config.ScaledResolutions.Where(addition => addition.Valid
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
                Available = available,
                Original = catalog is null ? null : config.ScaledResolutionBackups.FirstOrDefault(item => Belongs(item, catalog))
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

    internal static bool TryParseResolution(string text, out GpuScaledResolution resolution)
    {
        var dimensions = text.Split(['×', 'x', 'X'], StringSplitOptions.TrimEntries);
        if (dimensions.Length == 2
            && int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            resolution = new GpuScaledResolution(width, height);
            return resolution.Valid;
        }

        resolution = new GpuScaledResolution(0, 0);
        return false;
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
            var originalSaved = false;
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

                if (!originalSaved)
                {
                    RememberOriginal(fresh.Catalog!);
                    originalSaved = true;
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
        GpuScaledResolution mode, CancellationToken token = default)
    {
        await _edits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _store.Read().RequireConfig();
            var fresh = await ReadAsync(observed.Display.DevicePath, token).ConfigureAwait(false);
            if (!Same(observed, fresh.Catalog) || !fresh.Catalog!.Modes.Contains(mode))
            {
                return Refused("The driver list or display changed. Refresh before removing this resolution.");
            }

            if (mode == fresh.Native)
            {
                return Refused("The display's native resolution cannot be removed.");
            }

            var driver = await DriverAsync(observed, token).ConfigureAwait(false);
            if (driver is null)
            {
                return Refused("The driving adapter changed or its GPU integration is unavailable.");
            }

            RememberOriginal(fresh.Catalog!);
            var result = await driver.SetScaledResolutionAsync(observed, mode, remove: true, token).ConfigureAwait(false);
            if (result.Accepted)
            {
                ForgetAddition(observed, mode);
            }

            return result;
        }
        finally
        {
            _edits.Release();
        }
    }

    internal async Task<GpuScaledResolutionResult> RestoreOriginalAsync(GpuScaledResolutionCatalog observed,
        CancellationToken token = default)
    {
        await _edits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(observed.Display.DevicePath, token).ConfigureAwait(false);
            if (!Same(observed, state.Catalog) || state.Original is not { } original)
            {
                return Refused("No original list is saved for this display and driving adapter.");
            }

            var driver = await DriverAsync(observed, token).ConfigureAwait(false);
            if (driver is null)
            {
                return Refused("The driving GPU integration is unavailable.");
            }

            var changes = RestoreEdits(original, state.Catalog!.Modes, state.Native);
            var changed = false;
            var last = new GpuScaledResolutionResult(true, false, GpuScaledResolutionActivation.Immediate,
                "The original resolution list is already restored.");
            foreach (var (mode, remove) in changes)
            {
                last = await driver.SetScaledResolutionAsync(observed, mode, remove, token).ConfigureAwait(false);
                changed |= last.Changed;
                if (!last.Accepted)
                {
                    return last with { Changed = changed, Detail = (changed ? "Earlier changes were saved. " : "") + last.Detail };
                }

                if (remove)
                {
                    ForgetAddition(observed, mode);
                }
            }

            return last with { Changed = changed, Detail = changed ? "Original resolution list restored. " + last.Detail : last.Detail };
        }
        finally
        {
            _edits.Release();
        }
    }

    internal static IReadOnlyList<(GpuScaledResolution Mode, bool Remove)> RestoreEdits(
        ScaledResolutionBackup original, IReadOnlyList<GpuScaledResolution> current, GpuScaledResolution? native)
    {
        var modes = original.Modes.Select(mode => new GpuScaledResolution(mode.Width, mode.Height)).ToArray();
        return modes.Except(current).Select(mode => (Mode: mode, Remove: false))
            .Concat(current.Except(modes).Where(mode => mode != native).Select(mode => (Mode: mode, Remove: true)))
            .ToArray();
    }

    private void RememberOriginal(GpuScaledResolutionCatalog catalog)
    {
        _store.Update(config =>
        {
            if (config.ScaledResolutionBackups.Any(item => Belongs(item, catalog)))
            {
                return false;
            }

            config.ScaledResolutionBackups.Add(new ScaledResolutionBackup
            {
                DisplayPath = catalog.Display.DevicePath!, EdidIdentity = catalog.Display.EdidIdentity,
                AdapterIdentity = catalog.AdapterIdentity, OutputSpecific = catalog.OutputSpecific,
                Modes = catalog.Modes.Select(mode => new ScaledResolutionSize(mode.Width, mode.Height)).Distinct().ToList()
            });
            return true;
        });
    }

    private void ForgetAddition(GpuScaledResolutionCatalog catalog, GpuScaledResolution mode) =>
        _store.Update(config => config.ScaledResolutions.RemoveAll(item => Belongs(item, catalog)
            && item.Width == mode.Width && item.Height == mode.Height) > 0);

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

    private static bool Belongs(ScaledResolutionBackup backup, GpuScaledResolutionCatalog catalog) =>
        backup.AdapterIdentity == catalog.AdapterIdentity && backup.OutputSpecific == catalog.OutputSpecific
        && (!catalog.OutputSpecific || string.Equals(backup.DisplayPath, catalog.Display.DevicePath, StringComparison.OrdinalIgnoreCase)
            && backup.EdidIdentity == catalog.Display.EdidIdentity);

    private static GpuScaledResolutionResult Refused(string detail)
    {
        return new GpuScaledResolutionResult(false, false, GpuScaledResolutionActivation.Immediate, detail);
    }
}
