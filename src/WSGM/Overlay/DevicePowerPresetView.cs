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
    private readonly ComboBox _ac = AssignmentChoice("device.power-assignment.ac", "When plugged in");
    private readonly ComboBox _battery = AssignmentChoice("device.power-assignment.battery", "On battery");
    private readonly TextBlock _scope = new() { Classes = { "caption" } };
    private readonly StackPanel _assignments = new() { Spacing = 6 };
    private DevicePowerPreset[] _assignmentItems = [];
    private DevicePowerPresetSelection? _model;
    private DevicePowerPreset[] _items = [];
    private bool _rendering;

    /// <summary>Creates the persistent dropdown. A selection reports intent to the shared service.</summary>
    public DevicePowerPresetView()
    {
        AutomationProperties.SetName(_choices, "Device power profile");
        _assignments.Children.Add(_scope);
        _assignments.Children.Add(new TextBlock { Text = "When plugged in", Classes = { "setting-title" } });
        _assignments.Children.Add(_ac);
        _assignments.Children.Add(new TextBlock { Text = "On battery", Classes = { "setting-title" } });
        _assignments.Children.Add(_battery);
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
                    _assignments,
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
        _ac.SelectionChanged += async (_, _) =>
        {
            if (!_rendering && _model is { CanAssign: true } model && _ac.SelectedItem is DevicePowerPreset choice)
            { await model.AssignAsync(true, choice.Id.Length == 0 ? null : choice.Id); }
        };
        _battery.SelectionChanged += async (_, _) =>
        {
            if (!_rendering && _model is { CanAssign: true } model && _battery.SelectedItem is DevicePowerPreset choice)
            { await model.AssignAsync(false, choice.Id.Length == 0 ? null : choice.Id); }
        };
    }

    private static ComboBox AssignmentChoice(string tag, string name)
    {
        var choice = new ComboBox
        {
            Tag = tag,
            DisplayMemberBinding = new Binding(nameof(DevicePowerPreset.Name)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(choice, name);
        return choice;
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
            var assignments = _model?.Assignments;
            _assignments.IsVisible = assignments is not null;
            if (assignments is not null)
            {
                _scope.Text = assignments.Scope;
                DevicePowerPreset[] choices = [new("", assignments.Scope.StartsWith("Global", System.StringComparison.Ordinal)
                    ? "Manual selection" : "Use global assignment", 0, 0, DevicePowerMode.Balanced), .. state!.Presets];
                if (!_assignmentItems.SequenceEqual(choices))
                {
                    _assignmentItems = choices;
                    _ac.ItemsSource = choices;
                    _battery.ItemsSource = choices;
                }
                _ac.SelectedItem = _assignmentItems.FirstOrDefault(item => item.Id == (assignments.AcPreset ?? ""));
                _battery.SelectedItem = _assignmentItems.FirstOrDefault(item => item.Id == (assignments.BatteryPreset ?? ""));
                _ac.IsEnabled = _battery.IsEnabled = _model?.CanAssign == true;
            }
            _status.Text = _model?.Busy == true ? "Applying power profile..."
                : assignments?.Status.Length > 0 ? assignments.Status : state?.Status;
            IsVisible = state?.Presets.Count > 0;
        }
        finally { _rendering = false; }
    }
}
