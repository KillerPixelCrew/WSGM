using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>
///     A command row that leads with content of its own, such as a game's capsule art or a radio mark, and ends with
///     status chips: the collection row of a library list. The whole row is one focus and activation target.
/// </summary>
/// <remarks>
///     The leading control is kept across publications while its tag is unchanged, so a loaded preview is not
///     decoded again; the labels, line under the title and chips follow every refresh.
/// </remarks>
internal sealed class OverlayMediaRow : ActionButton, IOverlayRefreshable
{
    private readonly ContentControl _leading = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _line = new() { Focusable = false };

    private readonly WrapPanel _trailing = new()
    {
        Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right
    };

    private Action? _activate;

    /// <summary>Creates the row.</summary>
    /// <param name="title">The row's name, also its accessible name.</param>
    /// <param name="description">The supporting line, or empty.</param>
    /// <param name="leading">The leading content, or null for none.</param>
    /// <param name="line">Content shown between the title and the description, or null for none.</param>
    /// <param name="chips">The status chips at the end of the row.</param>
    /// <param name="activate">Runs when the row is chosen, or null while it is unavailable.</param>
    internal OverlayMediaRow(string title, string description, Control? leading, Control? line,
        IReadOnlyList<Control> chips, Action? activate)
    {
        if (Content is Grid layout)
        {
            // The icon column holds the leading content; the hint column holds the chips.
            layout.Children[0].IsVisible = false;
            layout.Children.Add(_leading);
            layout.Children.Add(_trailing);
            Grid.SetColumn(_trailing, 2);
            if (layout.Children[1] is StackPanel labels)
            {
                labels.Children.Insert(1, _line);
            }
        }

        Title = title;
        Description = description;
        _leading.Content = leading;
        _leading.IsVisible = leading is not null;
        _line.Content = line;
        _line.IsVisible = line is not null;
        SetChips(chips);
        _activate = activate;
        IsEnabled = activate is not null;
        Click += (_, _) => _activate?.Invoke();
    }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var next = (OverlayMediaRow)replacement;
        Title = next.Title;
        Description = next.Description;
        IsEnabled = next.IsEnabled;
        _activate = next._activate;
        Classes.Set("primary", next.Classes.Contains("primary"));
        if (!Equals((_leading.Content as Control)?.Tag, (next._leading.Content as Control)?.Tag)
            || _leading.Content is null != next._leading.Content is null)
        {
            var content = next._leading.Content;
            next._leading.Content = null;
            _leading.Content = content;
            _leading.IsVisible = content is not null;
        }

        var line = next._line.Content;
        next._line.Content = null;
        _line.Content = line;
        _line.IsVisible = line is not null;
        var chips = next._trailing.Children.ToArray();
        next._trailing.Children.Clear();
        SetChips(chips);
    }

    private void SetChips(IReadOnlyList<Control> chips)
    {
        _trailing.Children.Clear();
        foreach (var chip in chips)
        {
            chip.Margin = new Thickness(8, 2, 0, 2);
            _trailing.Children.Add(chip);
        }

        _trailing.IsVisible = chips.Count > 0;
    }
}
