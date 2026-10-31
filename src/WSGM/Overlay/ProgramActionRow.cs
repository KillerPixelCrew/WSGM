using System;
using Avalonia;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One shared presentation for a page action and its home pin, with a borrowed cached application icon.</summary>
internal sealed class ProgramActionRow : ActionButton, IOverlayRefreshable
{
    private readonly Image _image = new() { Width = 32, Height = 32 };
    private readonly Control? _fallback;
    private Action _activate;

    internal ProgramActionRow(ProgramState state, Action activate)
    {
        _activate = activate;
        if (Content is Grid layout)
        {
            _fallback = layout.Children[0];
            layout.Children.RemoveAt(0);
            var icon = new Grid();
            icon.Children.Add(_fallback);
            icon.Children.Add(_image);
            layout.Children.Insert(0, icon);
        }

        IconGeometry = Icons.Rocket;
        Apply(state);
        Click += (_, _) => _activate();
    }

    internal void Apply(ProgramState state)
    {
        Title = state.Definition.Name;
        Description = state.Status;
        TrailingText = state.Running ? "Focus" : state.CanLaunch ? "Launch" : "Review";
        _image.Source = state.Icon;
        _image.IsVisible = state.Icon is not null;
        if (_fallback is not null)
        {
            _fallback.IsVisible = state.Icon is null;
        }
    }

    internal void ReleaseIcon()
    {
        _image.Source = null;
        _image.IsVisible = false;
        if (_fallback is not null)
        {
            _fallback.IsVisible = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_fallback is not null)
        {
            _fallback.IsVisible = _image.Source is null;
        }
    }

    public void RefreshFrom(Control replacement)
    {
        var row = (ProgramActionRow)replacement;
        Title = row.Title;
        Description = row.Description;
        TrailingText = row.TrailingText;
        IsPinned = row.IsPinned;
        _image.Source = row._image.Source;
        _image.IsVisible = row._image.IsVisible;
        if (_fallback is not null)
        {
            _fallback.IsVisible = !_image.IsVisible;
        }

        _activate = row._activate;
    }
}
