using System;
using System.Collections.Generic;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     One hotkey as a command: its name, its keys as keycaps at the trailing edge and its last refusal. The
///     Hotkeys page and the Quick access tile share it, so both press the same send.
/// </summary>
/// <remarks>
///     The page shows the refusal in full and a Send hint. A tile shows the refusal's few-word summary and no
///     hint, so it keeps the height of the other tiles in the uniform Quick access grid.
/// </remarks>
internal sealed class HotkeyActionRow : ActionButton, IOverlayRefreshable
{
    private readonly bool _tile;
    private Action _activate;
    private string _chord = "";
    private IReadOnlyList<int> _keys = [];

    /// <summary>Draws a hotkey.</summary>
    /// <param name="state">The hotkey.</param>
    /// <param name="activate">What pressing it does.</param>
    /// <param name="tile">Whether it is the Quick access tile, which shows the short refusal.</param>
    internal HotkeyActionRow(HotkeyState state, Action activate, bool tile = false)
    {
        _activate = activate;
        _tile = tile;
        IconGeometry = Icons.Keyboard;
        // The page says what pressing does; a tile's keycaps already do, in half the width.
        TrailingText = tile ? null : "Send";
        if (tile)
        {
            Classes.Add("tile");
        }

        Apply(state);
        Click += (_, _) => _activate();
    }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var row = (HotkeyActionRow)replacement;
        CopyContentFrom(row);
        IsPinned = row.IsPinned;
        SetKeys(row._keys);
        _activate = row._activate;
    }

    /// <summary>Shows a hotkey's current name, keys and refusal.</summary>
    /// <param name="state">The hotkey.</param>
    internal void Apply(HotkeyState state)
    {
        Title = state.Definition.Name;
        var notice = _tile && state.Summary.Length > 0 ? state.Summary : state.Notice;
        Description = notice.Length > 0 ? notice : null;
        SetKeys(state.Definition.Keys);
    }

    private void SetKeys(IReadOnlyList<int> keys)
    {
        var chord = HotkeyKeys.Describe(keys);
        if (chord == _chord && TrailingContent is not null)
        {
            return;
        }

        _chord = chord;
        _keys = [.. keys];
        TrailingContent = Keycaps.Create(keys);
    }
}
