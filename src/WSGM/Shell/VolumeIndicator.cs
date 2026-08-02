using System;
using Avalonia.Threading;

namespace WSGM.Shell;

/// <summary>Owns the short-lived, non-activating game-mode volume OSD. Repeated
/// presses update one window and reset its dismissal timer instead of producing a
/// stack of top-level windows.</summary>
internal sealed class VolumeIndicator : IDisposable
{
    private static readonly TimeSpan DismissDelay = TimeSpan.FromSeconds(2);

    private readonly Func<double> _uiScale;
    private VolumeIndicatorWindow? _window;
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

        _window ??= new VolumeIndicatorWindow(_uiScale());
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
