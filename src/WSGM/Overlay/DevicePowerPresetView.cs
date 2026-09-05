using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>Device power presets, separate from Windows power plans and application preferences.</summary>
public sealed class DevicePowerPresetView : UserControl
{
    private readonly ComboBox _choices = new()
    {
        DisplayMemberBinding = new Binding(nameof(DevicePowerPreset.Name)),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        Tag = "device.power-preset.choice",
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private DevicePowerPresetSelection? _model;
    private DevicePowerPreset[] _items = [];
    private bool _rendering;

    /// <summary>Creates the persistent dropdown. A selection reports intent to the shared service.</summary>
    public DevicePowerPresetView()
    {
        AutomationProperties.SetName(_choices, "Device power profile");
        Content = new Border
        {
            Classes = { "tile" },
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Device power profile", Classes = { "setting-title" } },
                    _choices,
                    _status,
                },
            },
        };
        _choices.SelectionChanged += async (_, _) =>
        {
            if (!_rendering && _model is { CanSelect: true } model
                && _choices.SelectedItem is DevicePowerPreset choice && choice.Id != "custom"
                && choice.Id != model.State.Current)
            {
                await model.ApplyAsync(choice.Id);
            }
        };
    }

    internal void Attach(DevicePowerPresetSelection? model)
    {
        if (_model is not null) { _model.Changed -= Render; }
        _model = model;
        if (_model is not null) { _model.Changed += Render; }
        Render();
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            var state = _model?.State;
            DevicePowerPreset[] items = state is null ? [] : state.Current == "custom"
                ? [new("custom", "Custom", 0, 0, DevicePowerMode.Balanced), .. state.Presets]
                : [.. state.Presets];
            if (!_items.SequenceEqual(items))
            {
                _items = items;
                _choices.ItemsSource = items;
            }
            DevicePowerPreset? selected = _items.FirstOrDefault(item => item.Id == state?.Current);
            if (!Equals(_choices.SelectedItem, selected)) { _choices.SelectedItem = selected; }
            _choices.IsEnabled = _model?.CanSelect == true;
            _status.Text = _model?.Busy == true ? "Applying power profile..." : state?.Status;
            IsVisible = state?.Presets.Count > 0;
        }
        finally { _rendering = false; }
    }
}
