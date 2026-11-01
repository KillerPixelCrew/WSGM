using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     A Shell choice row (<see cref="ChoiceRowState" />) drawn as the overlay's dropdown setting row. It draws
///     the same options, selection and status Steam's row does, commits a settled selection and keeps an open
///     dropdown or focused editor through a refresh.
/// </summary>
public sealed class ChoiceRowView : ContentControl
{
    private ProfileOverrideMarker? _marker;
    private ChoiceRowSelection? _model;
    private DeviceSettingRow? _row;
    private Action<string>? _select;
    private ChoiceRowOption[] _shown = [];

    /// <summary>Creates an empty row; it draws once a selection is attached or a state is rendered.</summary>
    public ChoiceRowView()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>Follows one selection's published state, or stops following with null.</summary>
    /// <param name="model">The selection to draw and report choices to.</param>
    internal void Attach(ChoiceRowSelection? model)
    {
        if (_model is not null)
        {
            _model.Changed -= Render;
        }

        _model = model;
        if (model is not null)
        {
            model.Changed += Render;
        }

        Render();
    }

    private void Render()
    {
        if (_model is not { } model)
        {
            return;
        }

        Render(model.Key, model.Title, model.State, model.CanSelect, model.Status,
            option => _ = model.SelectAsync(option));
    }

    /// <summary>Draws one published state.</summary>
    /// <param name="key">The row's stable focus key.</param>
    /// <param name="title">The row's visible and accessible name.</param>
    /// <param name="state">The Shell projection; an unselectable option is listed only while it is current.</param>
    /// <param name="enabled">Whether the row accepts a choice now.</param>
    /// <param name="description">The line under the title.</param>
    /// <param name="select">Receives a changed, selectable option id.</param>
    internal void Render(string key, string title, ChoiceRowState state, bool enabled, string description,
        Action<string> select)
    {
        _select = select;
        ChoiceRowOption[] shown =
            [.. state.Options.Where(option => option.Selectable || option.Id == state.Current)];
        if (_row is null || !Equals(_row.Tag, key) || !Equals(_row.Content, title) || !shown.SequenceEqual(_shown))
        {
            if (_row?.Editor is ComboBox { IsDropDownOpen: true })
            {
                // The open list is the user's draft; the next refresh redraws it once it closes.
                return;
            }

            var focused = _row?.Editor.IsKeyboardFocusWithin == true;
            _shown = shown;
            _row = DeviceControlRows.Choice(key, title, description,
                [.. shown.Select(option => DeviceHostRows.HostChoice(option.Id, option.Label))], state.Current,
                enabled, option =>
                {
                    if (_shown.FirstOrDefault(candidate => candidate.Id == option) is { Selectable: true })
                    {
                        _select?.Invoke(option);
                    }
                });
            _marker = new ProfileOverrideMarker(_row, null);
            Content = _marker;
            if (focused)
            {
                _row.Editor.Focus(NavigationMethod.Directional);
            }
        }
        else
        {
            _row.Refresh(Choice(state.Current), enabled, description);
        }

        // The running game's own value is marked beside the row; its empty entry is the way back to Global.
        _marker!.Refresh(state.Accent ? key : null);
    }

    private static CapabilityValue Choice(string current)
    {
        return new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = current };
    }
}
