using System;
using Avalonia.Media.Imaging;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
///     One tray icon tile. Wraps the host's live record; Refresh() re-raises
///     the bindable projections after a NIM_MODIFY.
/// </summary>
public sealed class TrayIconEntry : ObservableObject
{
    private object? _image;
    private string _tip;

    /// <summary>Creates a tile over a live tray-icon record.</summary>
    /// <param name="icon">The host's icon record.</param>
    public TrayIconEntry(TrayIconRecord icon)
    {
        Icon = icon;
        _image = icon.IconImage;
        _tip = icon.Tip;
    }

    /// <summary>Gets the underlying tray-icon record (click forwarding target).</summary>
    public TrayIconRecord Icon { get; }

    /// <summary>Gets the rasterized icon image.</summary>
    public Bitmap? Image => Icon.IconImage as Bitmap;

    /// <summary>Gets the tooltip text.</summary>
    public string Tip => Icon.Tip;

    /// <summary>Re-raises the projections after the underlying record changed.</summary>
    /// <remarks>
    ///     The tray host replaces an icon's bitmap rather than drawing into it, so a reference
    ///     change is a real change; unchanged tiles are not rebound on every reconcile.
    /// </remarks>
    public void Refresh()
    {
        if (!ReferenceEquals(_image, Icon.IconImage))
        {
            _image = Icon.IconImage;
            Raise(nameof(Image));
        }

        if (string.Equals(_tip, Icon.Tip, StringComparison.Ordinal))
        {
            return;
        }

        _tip = Icon.Tip;
        Raise(nameof(Tip));
    }
}
