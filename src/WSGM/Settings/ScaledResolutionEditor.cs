using System;
using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>Controller-accessible editor over the same source-mode service used by Steam Display Settings.</summary>
public sealed class ScaledResolutionEditor : ObservableObject
{
    private readonly ScaledResolutionService? _service;
    private readonly CancellationTokenSource _lifetime = new();
    private ScaledResolutionState? _state;
    private bool _loading;
    private GpuScaledResolutionCatalog? _previewCatalog;
    private GpuScaledResolution? _previewNative;
    private GpuScaledResolution[] _preview = [];

    internal ScaledResolutionEditor(ScaledResolutionService? service)
    {
        _service = service;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PreviewCommand = new RelayCommand(Preview);
        AddCommand = new AsyncRelayCommand(AddAsync);
        ApplyPreviewCommand = new AsyncRelayCommand(ApplyPreviewAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        ReapplyCommand = new AsyncRelayCommand(ReapplyAsync);
        RestartCommand = new AsyncRelayCommand(RestartAsync);
    }

    /// <summary>Connected monitor choices.</summary>
    public ObservableCollection<ScaledResolutionChoice> Displays { get; } = [];
    /// <summary>Only additions remembered by WSGM, including entries missing after driver replacement.</summary>
    public ObservableCollection<ScaledResolutionAddition> Additions { get; } = [];
    /// <summary>Reads displays, driving adapter and custom source sizes.</summary>
    public AsyncRelayCommand RefreshCommand { get; }
    /// <summary>Builds the percentage preview without writing anything.</summary>
    public RelayCommand PreviewCommand { get; }
    /// <summary>Adds the entered width-height pair without selecting it.</summary>
    public AsyncRelayCommand AddCommand { get; }
    /// <summary>Adds the displayed percentage preview.</summary>
    public AsyncRelayCommand ApplyPreviewCommand { get; }
    /// <summary>Removes the selected WSGM addition.</summary>
    public AsyncRelayCommand RemoveCommand { get; }
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
                PreviewText = "";
                Log.Observe(RefreshAsync(), "Scaled resolution display selection");
            }
        }
    }

    /// <summary>Selected remembered addition.</summary>
    public ScaledResolutionAddition? Addition { get; set; }
    /// <summary>Entered pixel width.</summary>
    public string Width { get; set; } = "1760";
    /// <summary>Entered pixel height.</summary>
    public string Height { get; set; } = "1100";
    /// <summary>Whether an editor operation is running.</summary>
    public bool Busy
    {
        get;
        private set => SetField(ref field, value, nameof(Busy));
    }
    /// <summary>Driving GPU identity and list scope.</summary>
    public string AdapterText => _state?.Catalog is { } catalog
        ? catalog.AdapterName + " · " + catalog.AdapterIdentity + (catalog.OutputSpecific ? " · This output" : " · All outputs on this adapter")
        : "Driving GPU unavailable";
    /// <summary>Current native source-size basis.</summary>
    public string NativeText => _state?.Native is { } native ? $"Native {native.Width} × {native.Height}" : "Native size unavailable; percentage generation is disabled.";
    /// <summary>Existing custom catalog, including entries owned by other software.</summary>
    public string CatalogText => _state?.Catalog is { } catalog
        ? string.Join(", ", catalog.Modes.Select(mode => $"{mode.Width} × {mode.Height}")) : "";
    /// <summary>Last operation result or support explanation.</summary>
    public string Status
    {
        get;
        private set => SetField(ref field, value, nameof(Status));
    } = "Refresh to read the display's driving GPU.";
    /// <summary>Source-size percentage preview; constructing it writes nothing.</summary>
    public string PreviewText
    {
        get;
        private set => SetField(ref field, value, nameof(PreviewText));
    } = "";
    /// <summary>Whether the current list can be edited.</summary>
    public bool CanEdit => _state?.Catalog is { Supported: true };
    /// <summary>Whether remembered additions are missing.</summary>
    public bool HasMissing => _state?.Missing.Count > 0;
    /// <summary>Whether this vendor stores adapter lists requiring restart.</summary>
    public bool NeedsRestart => _state?.Catalog is { Supported: true, OutputSpecific: false };

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
        if (_previewCatalog is not null && (!ScaledResolutionService.Same(_previewCatalog, state.Catalog)
            || _previewNative != state.Native))
        {
            _preview = [];
            _previewCatalog = null;
            PreviewText = "";
        }
        _loading = true;
        try
        {
            Displays.Clear();
            foreach (var display in state.Displays)
            {
                Displays.Add(new ScaledResolutionChoice(display.Path, display.Name));
            }

            Display = Displays.FirstOrDefault(display => display.Path == state.SelectedPath);
            Additions.Clear();
            foreach (var addition in state.Remembered)
            {
                Additions.Add(addition);
            }

            Addition = Additions.FirstOrDefault();
            Raise(nameof(Addition));
            foreach (var name in new[] { nameof(AdapterText), nameof(NativeText), nameof(CatalogText), nameof(CanEdit), nameof(HasMissing), nameof(NeedsRestart) })
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

    private void Preview()
    {
        _previewNative = _state?.Native;
        _previewCatalog = _state?.Catalog;
        _preview = _previewNative is { } basis ? ScaledResolutionService.PercentagePreview(basis).ToArray() : [];
        PreviewText = _preview.Length > 0
            ? string.Join(Environment.NewLine, _preview
                .Select((mode, index) => $"{100 - index * 5}%: {mode.Width} × {mode.Height}"))
            : "Read the display's native size before generating percentages.";
    }

    private Task AddAsync()
    {
        return RunAsync(async () =>
        {
            if (_service is null || _state?.Catalog is not { Supported: true } catalog
                || !int.TryParse(Width, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(Height, NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            {
                Status = "Choose a supported display and enter pixel width and height.";
                return;
            }

            await FinishAsync(await _service.AddAsync(catalog, [new GpuScaledResolution(width, height)], _lifetime.Token));
        });
    }

    private Task ApplyPreviewAsync()
    {
        return RunAsync(async () =>
        {
            if (_service is not null && _state?.Catalog is { Supported: true } catalog
                && _previewCatalog is not null && ScaledResolutionService.Same(_previewCatalog, catalog)
                && _previewNative == _state.Native && _preview.Length > 0)
            {
                await FinishAsync(await _service.AddAsync(catalog, _preview, _lifetime.Token));
            }
        });
    }

    private Task RemoveAsync() => RunAsync(async () =>
    {
        if (_service is not null && _state?.Catalog is { } catalog && Addition is { } addition)
        {
            await FinishAsync(await _service.RemoveAsync(catalog, addition, _lifetime.Token));
        }
    });

    private Task ReapplyAsync() => RunAsync(async () =>
    {
        if (_service is not null && _state?.Catalog is { } catalog)
        {
            await FinishAsync(await _service.ReapplyAsync(catalog, _lifetime.Token));
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
