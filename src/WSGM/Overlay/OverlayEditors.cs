using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>UI-thread theme color editor that commits after 250 ms without another edit.</summary>
/// <remarks>Readback preserves an active draft. Detaching cancels a pending commit; this is not a device Apply control.</remarks>
internal sealed class OverlayColorPicker : ColorPicker, IOverlayRefreshable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Color _pending;
    private bool _refreshing;

    /// <summary>Creates the debounce owner; no color is written until a user change is observed.</summary>
    internal OverlayColorPicker()
    {
        ColorChanged += (_, e) =>
        {
            if (_refreshing)
            {
                return;
            }

            _pending = e.NewColor;
            _timer.Stop();
            _timer.Start();
        };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Commit?.Invoke(_pending);
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>Current owner callback for a settled user color; null leaves the editor presentation-only.</summary>
    internal Action<Color>? Commit { get; set; }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ColorPicker);

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var next = (OverlayColorPicker)replacement;
        Commit = next.Commit;
        if (IsKeyboardFocusWithin || _timer.IsEnabled)
        {
            return;
        }

        _refreshing = true;
        try
        {
            Color = next.Color;
        }
        finally
        {
            _refreshing = false;
        }
    }
}

/// <summary>Updates a retained UI control from a newly rendered description without replacing its focus identity.</summary>
internal interface IOverlayRefreshable
{
    /// <summary>Refreshes callbacks and non-editing state on the UI thread while retaining active drafts.</summary>
    /// <param name="replacement">An unmounted description of the same concrete control type and semantic key.</param>
    /// <remarks>The caller retains this control and discards the replacement; implementations must not adopt its lifetime.</remarks>
    void RefreshFrom(Control replacement);
}

/// <summary>Retained command row whose current callback follows service publications.</summary>
internal sealed class OverlayActionButton : ActionButton, IOverlayRefreshable
{
    /// <summary>Routes each click to the latest activation callback.</summary>
    internal OverlayActionButton()
    {
        Click += (_, _) => Activate?.Invoke();
    }

    /// <summary>Current command callback; null makes activation a no-op without changing enabled presentation.</summary>
    internal Action? Activate { get; set; }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var next = (OverlayActionButton)replacement;
        Title = next.Title;
        Description = next.Description;
        IconGeometry = next.IconGeometry;
        TrailingText = next.TrailingText;
        IsEnabled = next.IsEnabled;
        Activate = next.Activate;
        Classes.Set("primary", next.Classes.Contains("primary"));
        Classes.Set("danger", next.Classes.Contains("danger"));
    }
}

/// <summary>Retained choice editor that commits a changed selection when the dropdown closes or changes while closed.</summary>
/// <typeparam name="T">Semantic value compared with the default equality comparer.</typeparam>
internal sealed class OverlayChoice<T> : ComboBox, IOverlayRefreshable
{
    private Action<T> _commit;
    private T _committed;
    private bool _refreshing;

    /// <summary>Creates a UI-thread choice editor with the supplied value as its initial commit baseline.</summary>
    /// <param name="options">Available semantic values and visible labels.</param>
    /// <param name="current">Published value; no option is selected if it is absent from the list.</param>
    /// <param name="commit">Current owner callback for changed values; programmatic refresh does not invoke it.</param>
    internal OverlayChoice(IReadOnlyList<(T Value, string Label)> options, T current, Action<T> commit)
    {
        _commit = commit;
        _committed = current;
        ItemsSource = options.Select(option => new Option(option.Value, option.Label)).ToArray();
        SelectedItem =
            ((Option[])ItemsSource).FirstOrDefault(option => EqualityComparer<T>.Default.Equals(option.Value, current));
        SelectionChanged += (_, _) =>
        {
            if (!IsDropDownOpen)
            {
                Commit();
            }
        };
        DropDownClosed += (_, _) => Commit();
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var next = (OverlayChoice<T>)replacement;
        _commit = next._commit;
        IsEnabled = next.IsEnabled;
        if (IsDropDownOpen)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _committed = next._committed;
            ItemsSource = next.ItemsSource;
            SelectedItem = next.SelectedItem;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Commit()
    {
        if (_refreshing || SelectedItem is not Option selected ||
            EqualityComparer<T>.Default.Equals(_committed, selected.Value))
        {
            return;
        }

        _committed = selected.Value;
        _commit(selected.Value);
    }

    private sealed record Option(T Value, string Label)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Label;
        }
    }
}
