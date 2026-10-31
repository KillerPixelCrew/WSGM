using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

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
