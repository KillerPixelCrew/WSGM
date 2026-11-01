using Avalonia.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Tools > Performance: the shared performance rows while there is no Device destination to hold them.</summary>
/// <remarks>
///     With Device, the Power page and the Overview carry the rows through their own description; without it
///     they live here, with the per-application toggle the Overview would otherwise head. The rows' source owns
///     RTSS lifetime and state; this page only draws the shared group.
/// </remarks>
public sealed class PerformancePage : StackPanel
{
    private DeviceGroupRenderer? _renderer;

    /// <summary>Whether the rail offers the page: only while the rows live here, since an empty page is a dead end.</summary>
    internal bool Offered { get; private set; }

    /// <summary>Supplies the renderer every Device description is drawn with, once, before the first draw.</summary>
    /// <param name="renderer">The renderer.</param>
    internal void Configure(DeviceGroupRenderer renderer)
    {
        _renderer = renderer;
    }

    /// <summary>Draws the rows when they belong here and empties the page when they do not.</summary>
    /// <param name="snapshot">The performance snapshot, or null without a performance source.</param>
    /// <param name="deviceDestination">Whether the Device destination is offered, which then holds the rows.</param>
    internal void Show(PerformanceOverlaySnapshot? snapshot, bool deviceDestination)
    {
        var here = snapshot is { Visible: true } && !deviceDestination;
        Offered = here;
        _renderer?.Reconcile(this, here ? [DevicePages.Performance(snapshot!, true, false)] : [], false);
    }
}
