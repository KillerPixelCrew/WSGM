using Avalonia.Controls;
using Avalonia.Layout;
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

    private readonly OverlayActionButton _manage = new()
        {
            Title = "Saved presets", Tag = "device.power-preset.manage", IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

    private readonly OverlayActionButton _save = new()
        {
            Title = "Save as preset…", Tag = "device.power-preset.save", IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

    private readonly TextBlock _scope = new() { Classes = { "caption" } };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private DevicePowerPresetSelection? _model;

    /// <summary>Creates the assignment rows and the observed active-profile status.</summary>
    public DevicePowerPresetView()
    {
        _assignments.Children.Add(_scope);
        _assignments.Children.Add(_ac);
        _assignments.Children.Add(_battery);
        _assignments.Children.Add(_save);
        _assignments.Children.Add(_manage);
        _save.Activate = RequestSave;
        _manage.Activate = ShowSaved;
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
        // Saving is offered where the profile reads Custom; managing saved presets is a focused utility
        // rather than a stack of commands under every preset.
        _save.IsVisible = state.CanSave;
        _save.Description = "Current values: " + state.CurrentValues;
        _save.IsEnabled = model.CanEdit;
        // With nothing saved yet the Save row is the way in; the management utility appears once there is
        // something to manage, and says so itself when its last preset is deleted.
        var count = state.Saved?.Count ?? 0;
        _manage.IsVisible = count > 0;
        _manage.Description = count == 1
            ? "1 preset · Rename, update or delete"
            : $"{count} presets · Rename, update or delete";
        _manage.IsEnabled = model.CanEdit;
    }

    private void RequestSave()
    {
        if (_model is not { } model || model.State is not { CanSave: true } state)
        {
            return;
        }

        if (!OverlayWindow.RequestTextFor(this, "Preset name", state.SaveName, 0, name => _ = model.SaveAsync(name)))
        {
            _status.Text = "Keyboard unavailable. Reopen the overlay to retry.";
        }
    }

    private void ShowSaved()
    {
        if (_model is not { } model || TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var view = new DevicePowerSavedPresetsView(model);
        window.Surfaces.ShowUtility(view, "Saved presets", view.DefaultFocusTarget);
    }
}
