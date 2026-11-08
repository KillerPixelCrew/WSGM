using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>A command row that shows an application icon, falling back to its stroke glyph without one.</summary>
/// <remarks>The bitmap is borrowed: its owner disposes it after the row no longer shows it.</remarks>
internal class IconActionRow : ActionButton, IOverlayRefreshable
{
    private readonly Control? _fallback;
    private readonly Image _image = new() { Width = 32, Height = 32 };
    private Action _activate;

    /// <summary>Creates a row around the ordinary command layout.</summary>
    /// <param name="activate">What the row does when pressed.</param>
    internal IconActionRow(Action activate)
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
        Click += (_, _) => _activate();
    }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var row = (IconActionRow)replacement;
        Title = row.Title;
        Description = row.Description;
        TrailingText = row.TrailingText;
        IsPinned = row.IsPinned;
        ShowIcon(row._image.Source as Bitmap);
        _activate = row._activate;
    }

    /// <summary>Shows a borrowed icon, or the fallback glyph for null.</summary>
    /// <param name="icon">The icon; the caller keeps ownership.</param>
    internal void ShowIcon(Bitmap? icon)
    {
        _image.Source = icon;
        _image.IsVisible = icon is not null;
        if (_fallback is not null)
        {
            _fallback.IsVisible = icon is null;
        }
    }

    /// <summary>Lets go of the borrowed icon so its owner can dispose it.</summary>
    internal void ReleaseIcon()
    {
        ShowIcon(null);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_fallback is not null)
        {
            _fallback.IsVisible = _image.Source is null;
        }
    }
}
