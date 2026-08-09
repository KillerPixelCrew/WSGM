using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The taskbar's controller-friendly master-volume and default-device
/// panel. It remains a separate top-level window so combo-box popups and focus
/// traversal work while the taskbar is visible underneath.</summary>
public partial class AudioWindow : Window
{
    private const double BaseWidth = 500;
    private const double BaseHeight = 340;
    private readonly AudioManager _audio;
    private readonly double _uiScale;

    /// <summary>The slider receives controller focus when the panel opens.</summary>
    internal InputElement DefaultFocusTarget => VolumeSlider;

    /// <summary>Design-time constructor required by the Avalonia XAML loader.</summary>
    public AudioWindow()
        : this(new AudioManager())
    {
    }

    /// <summary>Creates an audio panel over the supplied live manager.</summary>
    /// <param name="audio">The taskbar-owned audio manager.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI.</param>
    public AudioWindow(AudioManager audio, double uiScale = 1.0)
    {
        _audio = audio;
        _uiScale = uiScale;
        InitializeComponent();
        DataContext = audio;
        Opened += (_, _) =>
        {
            _audio.Refresh();
            VolumeSlider.Focus(NavigationMethod.Directional);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        // Same delayed-touch defense as the taskbar and radio panel. The
        // controller owns the matching 150 ms deferred close.
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => _audio.Refresh();

    /// <summary>Places the panel just above the right-hand status section of the
    /// taskbar and scales it back to the user's normal desktop DPI.</summary>
    /// <param name="taskbarTop">The taskbar's physical top edge.</param>
    internal void DockAboveTaskbar(int taskbarTop = 0)
    {
        var factor = Math.Clamp(_uiScale / DesktopScaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"Audio panel UI scale {factor:0.##}x (desktop DPI over current {DesktopScaling:0.##}).");
            RootScale.LayoutTransform = new Avalonia.Media.ScaleTransform(factor, factor);
        }

        var screen = Screens.Primary ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }
        var area = screen.Bounds;
        var bottom = taskbarTop > 0 ? taskbarTop : area.Y + area.Height;
        Width = Math.Min(BaseWidth * factor, area.Width / DesktopScaling - 12);
        Height = Math.Min(BaseHeight * factor, (bottom - area.Y) / DesktopScaling - 8);
        UpdateLayout();

        var scale = DesktopScaling;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var gap = (int)Math.Round(2 * scale);
        var margin = (int)Math.Round(6 * scale);
        var x = area.X + area.Width - width - margin;
        var y = Math.Max(area.Y, bottom - height - gap);
        Position = new PixelPoint(x, y);
    }

    private static IntPtr WndProcHook(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg is Interop.NativeMethods.WmMouseMove
                or Interop.NativeMethods.WmLButtonDown
                or Interop.NativeMethods.WmLButtonUp)
        {
            var extra = (uint)Interop.NativeMethods.GetMessageExtraInfo();
            if ((extra & Interop.NativeMethods.MiWpSignatureMask)
                == Interop.NativeMethods.MiWpSignature)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }
}
