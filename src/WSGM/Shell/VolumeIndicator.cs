using System;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Owns the short-lived, non-activating game-mode volume OSD. Repeated
/// presses update one window and reset its dismissal timer instead of producing a
/// stack of top-level windows.</summary>
internal sealed class VolumeIndicator : IDisposable
{
    private static readonly TimeSpan DismissDelay = TimeSpan.FromSeconds(2);

    private readonly Func<double> _uiScale;
    private VolumeIndicatorWindow? _window;
    private double _windowScale;
    private DispatcherTimer? _dismissTimer;
    private bool _disposed;

    /// <summary>Creates an indicator using the caller's current game-mode UI scale.</summary>
    internal VolumeIndicator(Func<double> uiScale)
    {
        _uiScale = uiScale;
    }

    /// <summary>Shows the current master volume without taking focus.</summary>
    internal void Show(int percentage, bool muted)
    {
        if (_disposed)
        {
            return;
        }

        // The window bakes the scale in at construction and is only closed at shell
        // shutdown, so a scale that changed since (live config reload, or a mode
        // round trip re-applying the posture) has to recreate it — otherwise the OSD
        // keeps rendering at the factor that was current the first time it appeared.
        var scale = _uiScale();
        if (_window is not null && Math.Abs(scale - _windowScale) > 0.001)
        {
            Log.Info($"Volume OSD recreated for UI scale {_windowScale:0.##} -> {scale:0.##}.");
            _window.Close();
            _window = null;
        }
        if (_window is null)
        {
            _window = new VolumeIndicatorWindow(scale);
            _windowScale = scale;
        }
        _window.Update(percentage, muted);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_dismissTimer is null)
        {
            // Parameterless ctor + explicit Start: the callback ctor auto-starts.
            _dismissTimer = new DispatcherTimer { Interval = DismissDelay };
            _dismissTimer.Tick += (_, _) => Hide();
        }
        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    /// <summary>Hides the OSD immediately, used on the desktop-mode transition.</summary>
    internal void Hide()
    {
        _dismissTimer?.Stop();
        _window?.Hide();
    }

    /// <summary>Closes the OSD for shell shutdown.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _dismissTimer?.Stop();
        _dismissTimer = null;
        _window?.Close();
        _window = null;
    }
}
