using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>A connected monitor offered by the scaled-resolution editor.</summary>
public sealed record ScaledResolutionChoice(string Path, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A resolution in the selected display's list.</summary>
public sealed record ScaledResolutionItem(int Width, int Height, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>Controller-accessible editor over the same source-mode service used by Steam Display Settings.</summary>
public sealed class ScaledResolutionEditor : ObservableObject
{
    private readonly ScaledResolutionService? _service;
    private readonly CancellationTokenSource _lifetime = new();
    private ScaledResolutionState? _state;
    private bool _loading;

    internal ScaledResolutionEditor(ScaledResolutionService? service)
    {
        _service = service;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddCommand = new AsyncRelayCommand(AddAsync);
        AddStepsCommand = new AsyncRelayCommand(AddStepsAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync);
        ReapplyCommand = new AsyncRelayCommand(ReapplyAsync);
        RestartCommand = new AsyncRelayCommand(RestartAsync);
    }

    /// <summary>Connected monitor choices.</summary>
    public ObservableCollection<ScaledResolutionChoice> Displays { get; } = [];
    /// <summary>Editable source sizes reported by the driver.</summary>
    public ObservableCollection<ScaledResolutionItem> Resolutions { get; } = [];
    /// <summary>Reads displays, driving adapter and custom source sizes.</summary>
    public AsyncRelayCommand RefreshCommand { get; }
    /// <summary>Adds the entered width-height pair without selecting it.</summary>
    public AsyncRelayCommand AddCommand { get; }
    /// <summary>Adds native 100% down to 50%, in 5% steps.</summary>
    public AsyncRelayCommand AddStepsCommand { get; }
    /// <summary>Removes the selected driver-list entry.</summary>
    public AsyncRelayCommand RemoveCommand { get; }
    /// <summary>Restores the list captured before the first explicit edit.</summary>
    public AsyncRelayCommand RestoreCommand { get; }
    /// <summary>Explicitly reapplies missing remembered entries.</summary>
    public AsyncRelayCommand ReapplyCommand { get; }
    /// <summary>Explicitly restarts the selected display's graphics driver.</summary>
    public AsyncRelayCommand RestartCommand { get; }

    /// <summary>Selected connected monitor.</summary>
    public ScaledResolutionChoice? Display
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(Display)) && !_loading)
            {
                Log.Observe(RefreshAsync(), "Scaled resolution display selection");
            }
        }
    }

    /// <summary>Selected resolution; selection alone does not change the active display mode.</summary>
    public ScaledResolutionItem? Resolution
    {
        get;
        set
        {
            SetField(ref field, value, nameof(Resolution));
            Raise(nameof(CanRemove));
        }
    }
    /// <summary>Custom pixel dimensions, entered as width × height.</summary>
    public string Size
    {
        get;
        set
        {
            SetField(ref field, value, nameof(Size));
            Raise(nameof(CanAdd));
        }
    } = "";
    /// <summary>Whether an editor operation is running.</summary>
    public bool Busy
    {
        get;
        private set => SetField(ref field, value, nameof(Busy));
    }
    /// <summary>Driving GPU and native size.</summary>
    public string AdapterText => _state?.Catalog is { } catalog
        ? catalog.AdapterName + (_state.Native is { } native ? $" · Native {native.Width} × {native.Height}" : "")
        : "Driving GPU unavailable";
    /// <summary>Last operation result or support explanation.</summary>
    public string Status
    {
        get;
        private set => SetField(ref field, value, nameof(Status));
    } = "Refresh to read the display's driving GPU.";
    /// <summary>Whether the current list can be edited.</summary>
    public bool CanEdit => _state?.Catalog is { Supported: true };
    /// <summary>Whether the custom dimensions can be added.</summary>
    public bool CanAdd => CanEdit && ScaledResolutionService.TryParseResolution(Size, out _);
    /// <summary>Whether the native size is known for percentage generation.</summary>
    public bool CanAddSteps => CanEdit && _state?.Native is not null;
    /// <summary>Whether the selected size is in the driver's editable list.</summary>
    public bool CanRemove => CanEdit && Resolution is { } selected
        && new GpuScaledResolution(selected.Width, selected.Height) != _state!.Native
        && _state.Catalog!.Modes.Contains(new GpuScaledResolution(selected.Width, selected.Height));
    /// <summary>Whether an original list was saved for this driver route.</summary>
    public bool CanRestore => CanEdit && _state?.Original is not null;
    /// <summary>Whether remembered additions are missing.</summary>
    public bool HasMissing => _state?.Missing.Count > 0;
    /// <summary>Whether this vendor stores adapter lists requiring restart.</summary>
    public bool NeedsRestart => _state?.Catalog is { Supported: true, OutputSpecific: false };
    /// <summary>Whether optional driver maintenance is available.</summary>
    public bool HasDriverOptions => HasMissing || NeedsRestart;

    internal void Close() => _lifetime.Cancel();

    private async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            if (_service is not null)
            {
                await _service.RefreshTopologyAsync(_lifetime.Token);
            }

            await ReadAsync();
            Status = _state?.Detail ?? "No GPU owner is available in this Settings session.";
        });
    }

    private async Task ReadAsync()
    {
        if (_service is null)
        {
            Status = "The GPU integration is unavailable in this Settings session.";
            return;
        }

        var state = await _service.ReadAsync(Display?.Path, _lifetime.Token);
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        ApplySnapshot(state);
    }

    /// <summary>Publishes a detached snapshot on the editor's UI thread.</summary>
    internal void ApplySnapshot(ScaledResolutionState state)
    {
        _state = state;
        _loading = true;
        try
        {
            Displays.Clear();
            foreach (var display in state.Displays)
            {
                Displays.Add(new ScaledResolutionChoice(display.Path, display.Name));
            }

            Display = Displays.FirstOrDefault(display => display.Path == state.SelectedPath);
            var previous = Resolution;
            Resolutions.Clear();
            foreach (var mode in state.Resolutions)
            {
                Resolutions.Add(new ScaledResolutionItem(mode.Width, mode.Height, state.ResolutionLabel(mode)));
            }

            Resolution = Resolutions.FirstOrDefault(mode => mode.Width == previous?.Width && mode.Height == previous?.Height)
                ?? Resolutions.FirstOrDefault();
            foreach (var name in new[] { nameof(AdapterText), nameof(CanEdit), nameof(CanAdd), nameof(CanAddSteps), nameof(CanRestore), nameof(HasMissing), nameof(NeedsRestart), nameof(HasDriverOptions) })
            {
                Raise(name);
            }
        }
        finally
        {
            _loading = false;
        }

        Status = state.Detail;
    }

    private Task AddAsync()
    {
        return RunAsync(async () =>
        {
            if (_service is null || _state?.Catalog is not { Supported: true } catalog
                || !ScaledResolutionService.TryParseResolution(Size, out var mode))
            {
                Status = "Enter width × height, for example 1760 × 1100. Each dimension must be 100 to 15999 pixels.";
                return;
            }

            await FinishAsync(await _service.AddAsync(catalog, [mode], _lifetime.Token));
        });
    }

    private Task AddStepsAsync()
    {
        return RunAsync(async () =>
        {
            if (_service is not null && _state?.Catalog is { Supported: true } catalog
                && _state.Native is { } native)
            {
                await FinishAsync(await _service.AddAsync(catalog, ScaledResolutionService.PercentagePreview(native), _lifetime.Token));
            }
        });
    }

    private Task RemoveAsync() => RunAsync(async () =>
    {
        if (_service is not null && _state?.Catalog is { } catalog && Resolution is { } selected)
        {
            await FinishAsync(await _service.RemoveAsync(catalog,
                new GpuScaledResolution(selected.Width, selected.Height), _lifetime.Token));
        }
    });

    private Task ReapplyAsync() => RunAsync(async () =>
    {
        if (_service is not null && _state?.Catalog is { } catalog)
        {
            await FinishAsync(await _service.ReapplyAsync(catalog, _lifetime.Token));
        }
    });

    private Task RestoreAsync() => RunAsync(async () =>
    {
        if (_service is not null && _state?.Catalog is { } catalog)
        {
            await FinishAsync(await _service.RestoreOriginalAsync(catalog, _lifetime.Token));
        }
    });

    private Task RestartAsync() => RunAsync(async () =>
    {
        if (_service is not null && _state?.Catalog is { } catalog)
        {
            await FinishAsync(await _service.RestartAsync(catalog, _lifetime.Token));
        }
    });

    private async Task FinishAsync(GpuScaledResolutionResult result)
    {
        await ReadAsync();
        Status = result.Detail;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (Busy || _lifetime.IsCancellationRequested)
        {
            return;
        }

        Busy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Status = error.Message + " Refresh before another explicit action.";
        }
        finally
        {
            Busy = false;
        }
    }
}
