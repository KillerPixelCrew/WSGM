using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace WSGM.Controls;

/// <summary>
///     Wraps one row and marks it while the running game's own profile supplies its value.
/// </summary>
/// <remarks>
///     An accent bar beside the row and one line under it: "Game override" and a Use global button that
///     removes the game's value so the row falls back to Global. Nothing is drawn while the value comes
///     from anywhere else, so a row looks exactly as it did without per-game profiles. The owner supplies
///     what Use global does; this only reports the press.
/// </remarks>
internal sealed class ProfileOverrideMarker : Grid
{
    private readonly Border _bar = new()
    {
        Classes = { "profile-override-bar" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Stretch,
        // A margin rather than column spacing: without an override the row must lay out exactly as it
        // did before, and spacing would still indent it beside an empty column.
        Margin = new Thickness(0, 0, 8, 0)
    };

    private readonly StackPanel _line = new()
    {
        Orientation = Orientation.Horizontal, Spacing = 12, IsVisible = false, Margin = new Thickness(0, 4, 0, 0)
    };

    private readonly Func<string, Task>? _useGlobal;
    private readonly Button _useGlobalButton = new() { Content = "Use global", Classes = { "profile-override-reset" } };
    private bool _resetting;

    /// <summary>Wraps a row.</summary>
    /// <param name="row">The row to mark.</param>
    /// <param name="useGlobal">Removes the game's value for a setting id, or null to offer no action.</param>
    internal ProfileOverrideMarker(Control row, Func<string, Task>? useGlobal)
    {
        ArgumentNullException.ThrowIfNull(row);
        _useGlobal = useGlobal;
        Row = row;
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        RowDefinitions = new RowDefinitions("Auto,Auto");
        SetRowSpan(_bar, 2);
        Children.Add(_bar);
        SetColumn(row, 1);
        Children.Add(row);
        _line.Children.Add(new TextBlock
        {
            Text = "Game override", Classes = { "profile-override-label" }, VerticalAlignment = VerticalAlignment.Center
        });
        AutomationProperties.SetName(_useGlobalButton, "Use the Global value");
        _useGlobalButton.IsVisible = useGlobal is not null;
        _useGlobalButton.Click += async (_, _) => await ResetAsync();
        _line.Children.Add(_useGlobalButton);
        SetColumn(_line, 1);
        SetRow(_line, 1);
        Children.Add(_line);
    }

    /// <summary>The wrapped row.</summary>
    internal Control Row { get; }

    /// <summary>The setting id while the game's profile supplies the value, else null.</summary>
    internal string? OverrideId { get; private set; }

    /// <summary>Shows or hides the marker.</summary>
    /// <param name="overrideId">The setting id while the game's profile supplies the value, else null.</param>
    internal void Refresh(string? overrideId)
    {
        OverrideId = overrideId;
        var overridden = overrideId is not null;
        _bar.IsVisible = overridden;
        _line.IsVisible = overridden;
        _useGlobalButton.IsEnabled = overridden && !_resetting;
    }

    private async Task ResetAsync()
    {
        if (_resetting || OverrideId is not { } id || _useGlobal is null)
        {
            return;
        }

        var hadFocus = _useGlobalButton.IsFocused;
        _resetting = true;
        _useGlobalButton.IsEnabled = false;
        try
        {
            await _useGlobal(id);
        }
        finally
        {
            _resetting = false;
            _useGlobalButton.IsEnabled = OverrideId is not null;
            // The line disappears once the value is Global again; hand focus back to the row so a
            // controller does not lose its place.
            if (hadFocus && !_line.IsVisible)
            {
                Row.Focus(NavigationMethod.Directional);
            }
        }
    }
}
