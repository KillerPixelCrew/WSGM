using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WSGM.Settings;

/// <summary>Selectable, draggable screens for a saved display draft. It never writes Windows state.</summary>
public sealed class DisplayArrangementView : Canvas
{
    /// <summary>Identifies the draft displayed by this arrangement.</summary>
    public static readonly StyledProperty<DisplayLayoutEditor?> EditorProperty =
        AvaloniaProperty.Register<DisplayArrangementView, DisplayLayoutEditor?>(nameof(Editor));

    private readonly Dictionary<DisplayLayoutEditorRow, Button> _screens = [];
    private DisplayLayoutEditor? _subscribed;
    private double _scale = 1;
    private Point? _press;
    private Point _original;
    private Point _buttonOrigin;
    private bool _dragging;

    /// <summary>Gets or sets the layout being arranged.</summary>
    public DisplayLayoutEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty) { Subscribe(); }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); Subscribe(); }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribed is not null) { _subscribed.PropertyChanged -= Changed; _subscribed = null; }
        _press = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe()
    {
        if (_subscribed is not null) { _subscribed.PropertyChanged -= Changed; }
        _subscribed = Editor;
        if (_subscribed is not null) { _subscribed.PropertyChanged += Changed; }
        RefreshScreens();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => RefreshScreens();

    private void RefreshScreens()
    {
        if (Editor is not { } editor) { Children.Clear(); _screens.Clear(); return; }
        foreach (var row in _screens.Keys.Where(row => !editor.Rows.Contains(row)).ToArray())
        { Children.Remove(_screens[row]); _screens.Remove(row); }
        foreach (var row in editor.Rows)
        {
            if (!_screens.TryGetValue(row, out var button))
            {
                button = new Button { Classes = { "display-monitor" }, Tag = row };
                button.Click += (_, _) => editor.Selected = row;
                button.AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
                button.AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
                button.AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
                button.PointerCaptureLost += (_, _) => { _press = null; InvalidateArrange(); };
                _screens.Add(row, button);
                Children.Add(button);
            }
            button.IsVisible = row.Active && row.Mode is not null;
            button.Content = row.Number + (row.IsPrimary ? " ★" : "");
            button.Classes.Set("selected", editor.Selected == row);
            AutomationProperties.SetName(button, row.InspectorTitle + (row.IsPrimary ? ", primary" : ""));
        }
        InvalidateArrange();
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var active = _screens.Where(pair => pair.Key.Active && pair.Key.Mode is not null).ToArray();
        if (active.Length > 0 && _press is null)
        {
            double left = active.Min(pair => pair.Key.X), top = active.Min(pair => pair.Key.Y);
            var width = active.Max(pair => (double)pair.Key.X + pair.Key.Mode!.Width) - left;
            var height = active.Max(pair => (double)pair.Key.Y + pair.Key.Mode!.Height) - top;
            _scale = Math.Max(.001, Math.Min((finalSize.Width - 32) / width, (finalSize.Height - 24) / height));
            double offsetX = (finalSize.Width - width * _scale) / 2, offsetY = (finalSize.Height - height * _scale) / 2;
            foreach (var (row, button) in active)
            {
                button.Width = row.Mode!.Width * _scale;
                button.Height = row.Mode.Height * _scale;
                SetLeft(button, offsetX + (row.X - left) * _scale);
                SetTop(button, offsetY + (row.Y - top) * _scale);
            }
        }
        return base.ArrangeOverride(finalSize);
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button { Tag: DisplayLayoutEditorRow row } button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { return; }
        _press = e.GetPosition(this);
        _original = new Point(row.X, row.Y);
        _buttonOrigin = new Point(GetLeft(button), GetTop(button));
        _dragging = false;
        if (Editor is { } editor) { editor.Selected = row; }
        e.Pointer.Capture(button);
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } start || sender is not Button button) { return; }
        Vector delta = e.GetPosition(this) - start;
        if (!_dragging && Math.Abs(delta.X) + Math.Abs(delta.Y) < 5) { return; }
        _dragging = true;
        SetLeft(button, _buttonOrigin.X + delta.X);
        SetTop(button, _buttonOrigin.Y + delta.Y);
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is not { } start || sender is not Button { Tag: DisplayLayoutEditorRow row }) { return; }
        Vector delta = e.GetPosition(this) - start;
        _press = null;
        if (_dragging && Editor is { } editor && row.Mode is { } mode)
        {
            int x = (int)Math.Round(_original.X + delta.X / _scale), y = (int)Math.Round(_original.Y + delta.Y / _scale);
            foreach (var other in editor.Rows.Where(item => item != row && item.Active && item.Mode is not null))
            {
                foreach (var candidate in new[] { other.X - mode.Width, other.X + other.Mode!.Width })
                { if (Math.Abs((double)x - candidate) * _scale < 14) { x = candidate; } }
                foreach (var candidate in new[] { other.Y, other.Y + other.Mode!.Height - mode.Height,
                    other.Y - mode.Height, other.Y + other.Mode.Height })
                { if (Math.Abs((double)y - candidate) * _scale < 14) { y = candidate; } }
            }
            editor.Move(row, x, y);
            e.Handled = true;
        }
        e.Pointer.Capture(null);
        InvalidateArrange();
    }
}
