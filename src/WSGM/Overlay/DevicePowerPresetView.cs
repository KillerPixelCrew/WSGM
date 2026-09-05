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
    private readonly TextBlock _active = new() { Classes = { "setting-title" }, Tag = "device.power-preset.active" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private readonly ComboBox _ac = AssignmentChoice("device.power-assignment.ac", "When plugged in");
    private readonly ComboBox _battery = AssignmentChoice("device.power-assignment.battery", "On battery");
    private readonly TextBlock _scope = new() { Classes = { "caption" } };
    private readonly StackPanel _assignments = new() { Spacing = 6 };
    private DevicePowerPreset[] _assignmentItems = [];
    private DevicePowerPresetSelection? _model;
    private bool _rendering;

    /// <summary>Creates the assignment dropdowns and observed active-profile status.</summary>
    public DevicePowerPresetView()
    {
        _assignments.Children.Add(_scope);
        _assignments.Children.Add(new TextBlock { Text = "When plugged in", Classes = { "setting-title" } });
        _assignments.Children.Add(_ac);
        _assignments.Children.Add(new TextBlock { Text = "On battery", Classes = { "setting-title" } });
        _assignments.Children.Add(_battery);
        Content = new Border
        {
            Classes = { "device-group" },
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Device power profile", Classes = { "setting-title" } },
                    _active,
                    _assignments,
                    _status,
                },
            },
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
            _active.Text = "Active: " + (state?.Presets.FirstOrDefault(preset => preset.Id == state.Current)?.Name
                ?? (state?.Current == "custom" ? "Custom" : "Unavailable"));
            var assignments = _model?.Assignments;
            _assignments.IsVisible = assignments is not null;
            if (assignments is not null)
            {
                _scope.Text = assignments.Scope;
                DevicePowerPreset[] choices = [new("", assignments.Scope.StartsWith("Global", System.StringComparison.Ordinal)
                    ? "Manual selection" : "Use global assignment", 0, 0, DevicePowerMode.Balanced), .. state!.Presets];
                if (!_ac.IsDropDownOpen && !_battery.IsDropDownOpen && _model?.Busy != true
                    && !_assignmentItems.SequenceEqual(choices))
                {
                    _assignmentItems = choices;
                    _ac.ItemsSource = choices;
                    _battery.ItemsSource = choices;
                }
                if (!_ac.IsDropDownOpen && _model?.Busy != true)
                { _ac.SelectedItem = _assignmentItems.FirstOrDefault(item => item.Id == (assignments.AcPreset ?? "")); }
                if (!_battery.IsDropDownOpen && _model?.Busy != true)
                { _battery.SelectedItem = _assignmentItems.FirstOrDefault(item => item.Id == (assignments.BatteryPreset ?? "")); }
                _ac.IsEnabled = _battery.IsEnabled = _model?.CanAssign == true;
            }
            _status.Text = _model?.Busy == true ? "Applying power profile..."
                : assignments?.Status.Length > 0 ? assignments.Status : state?.Status;
            IsVisible = state?.Presets.Count > 0;
        }
        finally { _rendering = false; }
    }
}
