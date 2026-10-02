using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;

namespace WSGM.Overlay;

internal sealed class OverlayColorPicker : ColorPicker, IOverlayRefreshable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Color _pending;
    private bool _refreshing;

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

    internal Action<Color>? Commit { get; set; }
    protected override Type StyleKeyOverride => typeof(ColorPicker);

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

internal interface IOverlayRefreshable
{
    void RefreshFrom(Control replacement);
}

internal sealed class OverlayActionButton : ActionButton, IOverlayRefreshable
{
    internal OverlayActionButton()
    {
        Click += (_, _) => Activate?.Invoke();
    }

    internal Action? Activate { get; set; }

    public void RefreshFrom(Control replacement)
    {
        var next = (OverlayActionButton)replacement;
        Title = next.Title;
        Description = next.Description;
        IconGeometry = next.IconGeometry;
        IsEnabled = next.IsEnabled;
        Activate = next.Activate;
        Classes.Set("primary", next.Classes.Contains("primary"));
        Classes.Set("danger", next.Classes.Contains("danger"));
    }
}

internal sealed class OverlayChoice<T> : ComboBox, IOverlayRefreshable
{
    private Action<T> _commit;
    private T _committed;
    private bool _refreshing;

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

    protected override Type StyleKeyOverride => typeof(ComboBox);

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
        public override string ToString()
        {
            return Label;
        }
    }
}
