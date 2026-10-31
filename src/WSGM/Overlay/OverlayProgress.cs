using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>
///     A level's live progress element: one caption, a determinate or indeterminate bar and an optional Cancel row,
///     updated in place by <see cref="Apply" /> so progress ticks do not rebuild the level around it.
/// </summary>
/// <remarks>
///     <para>
///         The element stays mounted while hidden, so a level that registers live updates through
///         <see cref="ServiceSubView" /> can show work that starts without a structural rebuild. A redraw of the level
///         reconciles the retained element through <see cref="IOverlayRefreshable" />, keeping the Cancel row's focus.
///     </para>
///     <para>
///         The Cancel row keeps its own stable key, <c>{tag}.cancel</c> by default. When it disappears while
///         focused, focus moves to the level's first navigable control, the same target a redraw without it would
///         choose.
///     </para>
/// </remarks>
internal sealed class OverlayProgress : StackPanel, IOverlayRefreshable
{
    private readonly ProgressBar _bar;
    private readonly OverlayActionButton _cancel;
    private readonly TextBlock _text;

    /// <summary>Creates the element with its first state; call on the UI thread.</summary>
    /// <param name="tag">The element's stable semantic key within its level.</param>
    /// <param name="state">The state to show, or null to start hidden.</param>
    /// <param name="cancelTag">The Cancel row's stable key; null uses <c>{tag}.cancel</c>.</param>
    internal OverlayProgress(string tag, OverlayProgressState? state, string? cancelTag = null)
    {
        Tag = tag;
        Spacing = 6;
        _text = new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 0)
        };
        _bar = NewBar();
        _cancel = new OverlayActionButton { Tag = cancelTag ?? tag + ".cancel", IconGeometry = Icons.Close };
        Children.Add(_text);
        Children.Add(_bar);
        Children.Add(_cancel);
        Apply(state);
    }

    /// <summary>
    ///     Creates a capacity reading's meter: the same bar as an operation's progress, the Overlay's counterpart of
    ///     Steam's usage meter. The caption beside it states the amounts.
    /// </summary>
    /// <param name="used">The amount in use.</param>
    /// <param name="capacity">The whole; zero or less draws an empty bar.</param>
    /// <param name="name">The meter's accessible name.</param>
    /// <returns>The meter.</returns>
    internal static ProgressBar UsageMeter(long used, long capacity, string name)
    {
        var meter = NewBar();
        meter.Value = capacity > 0 ? Math.Clamp((double)used / capacity, 0, 1) : 0;
        AutomationProperties.SetName(meter, name);
        return meter;
    }

    // The shared bar: a thin accent fill from 0 to 1 across the pane.
    private static ProgressBar NewBar()
    {
        return new ProgressBar
        {
            Classes = { "overlay-progress" },
            Minimum = 0,
            Maximum = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(2, 0, 2, 4)
        };
    }

    /// <summary>The state shown now, or null while hidden.</summary>
    internal OverlayProgressState? State { get; private set; }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        Apply(((OverlayProgress)replacement).State);
    }

    /// <summary>Shows a new state in place; call on the UI thread.</summary>
    /// <param name="state">The state, or null to hide the element.</param>
    internal void Apply(OverlayProgressState? state)
    {
        State = state;
        var text = state?.Text ?? "";
        _text.Text = text;
        _text.IsVisible = text.Length > 0;

        var active = state?.Active == true;
        _bar.IsVisible = active;
        _bar.IsIndeterminate = active && state!.Fraction is null;
        _bar.Value = state?.Fraction is { } fraction ? Math.Clamp(fraction, 0, 1) : 0;
        AutomationProperties.SetName(_bar, text);

        var cancel = state?.Cancel;
        var refocus = cancel is null && _cancel.IsVisible && _cancel.IsKeyboardFocusWithin;
        _cancel.Title = cancel?.Title ?? "";
        _cancel.Description = cancel?.Description ?? "";
        _cancel.Activate = cancel?.Activate;
        _cancel.IsEnabled = cancel is not null;
        _cancel.IsVisible = cancel is not null;

        IsVisible = text.Length > 0 || active || cancel is not null;
        if (refocus && (this.FindLogicalAncestorOfType<OverlaySubView>()?.Content as Visual
                        ?? this.GetVisualParent()) is { } level)
        {
            FocusSearch.FirstNavigable(level)?.Focus(NavigationMethod.Directional);
        }
    }
}
