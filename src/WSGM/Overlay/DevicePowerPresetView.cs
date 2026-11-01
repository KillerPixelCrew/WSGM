using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Device power presets, separate from Windows power plans and application preferences. It draws the
///     <see cref="PowerPresetRowState" /> Steam's Device performance rows draw: the preset in effect and the
///     plugged-in and battery assignments with the same choices, labels and game-override marks.
/// </summary>
public sealed class DevicePowerPresetView : UserControl
{
    private readonly ChoiceRowView _ac = new();
    private readonly TextBlock _active = new() { Classes = { "setting-title" }, Tag = "device.power-preset.active" };
    private readonly StackPanel _assignments = new() { Spacing = 6 };
    private readonly ChoiceRowView _battery = new();
    private readonly TextBlock _scope = new() { Classes = { "caption" } };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private DevicePowerPresetSelection? _model;

    /// <summary>Creates the assignment rows and the observed active-profile status.</summary>
    public DevicePowerPresetView()
    {
        _assignments.Children.Add(_scope);
        _assignments.Children.Add(_ac);
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
                    _active,
                    _assignments,
                    _status
                }
            }
        };
    }

    internal void Attach(DevicePowerPresetSelection? model)
    {
        if (_model is not null)
        {
            _model.Changed -= Render;
        }

        _model = model;
        if (_model is not null)
        {
            _model.Changed += Render;
        }

        Render();
    }

    private void Render()
    {
        var model = _model;
        var state = model?.State;
        _active.Text = "Active: " + (state?.Current is { Length: > 0 } current ? current : "Unavailable");
        _status.Text = model?.Busy == true ? "Applying power profile..." : state?.StatusText;
        IsVisible = state?.Options.Count > 0;
        // While an assignment runs, the rows keep the user's choice on screen rather than the value before it.
        _assignments.IsEnabled = model is { Busy: false };
        if (model is null || state is null || model.Busy)
        {
            return;
        }

        _scope.Text = state.Scope;
        _scope.IsVisible = state.Scope.Length > 0;
        // The empty entry is the way back to Global for a game's own assignment, so an override needs only
        // its marker, not a second control.
        ChoiceRowOption[] options =
        [
            new(string.Empty, state.UnsetLabel is { Length: > 0 } unset ? unset : "Manual selection"),
            .. state.Options
        ];
        _ac.Render("device.power-assignment.ac", "When plugged in",
            new ChoiceRowState(state.Available, options, state.Ac, string.Empty, state.AcAccent), model.CanAssign,
            string.Empty, id => _ = model.AssignAsync(true, id.Length == 0 ? null : id));
        _battery.Render("device.power-assignment.battery", "On battery",
            new ChoiceRowState(state.Available, options, state.Battery, string.Empty, state.BatteryAccent),
            model.CanAssign, string.Empty, id => _ = model.AssignAsync(false, id.Length == 0 ? null : id));
    }
}
