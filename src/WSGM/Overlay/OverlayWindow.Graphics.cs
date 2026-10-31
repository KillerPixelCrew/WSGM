using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Device GPU section: each section a graphics package declares, an adapter or a
///     display, drawn with the Device destination's groups and rows.
/// </summary>
/// <remarks>
///     The rows are the same controls as a Device section's, so a toggle, slider, choice or reading
///     behaves, marks a game override and offers Use global exactly as a device row does. Only the source
///     differs: writes go to the graphics coordinator through <see cref="IGraphicsOverlaySource" />, and
///     the destination follows the graphics packages rather than the device integration switch.
/// </remarks>
public partial class OverlayWindow
{
    private IGraphicsOverlaySource? _graphicsSource;

    internal void AttachGraphicsSource(IGraphicsOverlaySource? source)
    {
        if (ReferenceEquals(_graphicsSource, source))
        {
            return;
        }

        if (_graphicsSource is not null)
        {
            _graphicsSource.Changed -= OnGraphicsChanged;
        }

        _graphicsSource = source;
        if (_graphicsSource is not null)
        {
            _graphicsSource.Changed += OnGraphicsChanged;
        }

        var snapshot = _graphicsSource?.Snapshot() ?? GraphicsOverlaySnapshot.Empty;
        ConfigureGpuDestination(snapshot.Visible);
        // Graphics groups are drawn on the GPU page, offered on the Overview and drawn on Quick access.
        RefreshDevicePanel();
        RenderPins(graphics: snapshot);
    }

    private void DetachGraphicsSource()
    {
        if (_graphicsSource is not null)
        {
            _graphicsSource.Changed -= OnGraphicsChanged;
        }
    }

    private void OnGraphicsChanged()
    {
        QueueLiveRefresh(GraphicsLiveRefresh);
    }
}
