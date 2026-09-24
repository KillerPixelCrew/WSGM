using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Avalonia.LiveBackdrop;

/// <summary>
///     Owns one live Windows desktop backdrop behind an Avalonia window. All calls belong on the UI thread.
/// </summary>
/// <remarks>
///     Configure the window with Transparent transparency and a transparent background before showing it.
///     This component temporarily makes it a tool window and excludes all windows in this process from the
///     backdrop. Windows 11 x64 and the native library are required. Private DWM exports are not a supported
///     Windows contract; inspect <see cref="IsActive" /> and <see cref="FailureReason" /> for fallback state.
/// </remarks>
public sealed class LiveBackdrop : IDisposable
{
    private static readonly ConditionalWeakTable<Window, LiveBackdrop> Attachments = new();
    private readonly IBrush _fallback;
    private readonly IBrush? _originalBackground;
    private readonly Window _window;
    private double _blurRadius;
    private NativeMethods.FailureCallback? _callback;
    private bool _disposed;
    private bool _enabled = true;
    private int _generation;
    private nint _session;

    private LiveBackdrop(Window window, double blurRadius, IBrush fallback)
    {
        _window = window;
        _blurRadius = blurRadius;
        _fallback = fallback;
        _originalBackground = window.Background;
        window.Opened += OnOpened;
        window.Closed += OnClosed;
        window.PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>Gets whether the native backdrop is currently active.</summary>
    public bool IsActive => _session != 0;

    /// <summary>Gets the last failure, or null when there is no failure. Failed sessions require <see cref="Retry" />.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Gets or sets Gaussian standard deviation in physical pixels, from 0 to 60. The default is 8.</summary>
    public double BlurRadius
    {
        get => _blurRadius;
        set
        {
            VerifyAccess();
            ValidateBlur(value);
            _blurRadius = value;
            if (_session == 0)
            {
                return;
            }

            var result = NativeMethods.BackdropSetBlur(_session, (float)value);
            if (result < 0)
            {
                SetFailure($"Blur update failed (0x{result:X8}).");
            }
        }
    }

    /// <summary>Gets or sets whether the backdrop is enabled. Disabled windows use the fallback fill.</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            VerifyAccess();
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Reconcile();
        }
    }

    /// <summary>Releases hooks, native windows and GPU resources, restoring the window's original background and style bits.</summary>
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Opened -= OnOpened;
        _window.Closed -= OnClosed;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        Stop();
        _window.Background = _originalBackground;
        Attachments.Remove(_window);
    }

    /// <summary>Raised on the UI thread when active, disabled or failed state changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Attaches a single backdrop owner to a window, before or after it opens.</summary>
    /// <param name="window">The transparent Avalonia window.</param>
    /// <param name="blurRadius">Gaussian standard deviation in physical pixels.</param>
    /// <param name="fallback">Opaque fill used when disabled or unavailable; defaults to dark gray.</param>
    /// <returns>An attachment disposed automatically on close, or explicitly by its owner.</returns>
    public static LiveBackdrop Attach(Window window, double blurRadius = 8, IBrush? fallback = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(window);
        ValidateBlur(blurRadius);
        if (Attachments.TryGetValue(window, out _))
        {
            throw new InvalidOperationException("A live backdrop is already attached to this window.");
        }

        var attachment = new LiveBackdrop(window, blurRadius,
            fallback ?? new SolidColorBrush(Color.FromRgb(24, 27, 32)));
        Attachments.Add(window, attachment);
        attachment.Reconcile();
        return attachment;
    }

    /// <summary>Explicitly retries after a missing capability, device failure or display change.</summary>
    public void Retry()
    {
        VerifyAccess();
        Stop();
        FailureReason = null;
        Reconcile();
    }

    private static void ValidateBlur(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Blur must be between 0 and 60 pixels.");
        }
    }

    private void VerifyAccess()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void OnOpened(object? sender, EventArgs args)
    {
        Reconcile();
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        Dispose();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.IsVisibleProperty || args.Property == Window.WindowStateProperty
                                                      || args.Property == TopLevel.ActualTransparencyLevelProperty)
        {
            Reconcile();
        }
    }

    private void Reconcile()
    {
        if (_disposed)
        {
            return;
        }

        if (!_enabled || !_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            Stop();
            _window.Background = _fallback;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (FailureReason is not null || _session != 0)
        {
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            SetFailure("Live backdrops require Windows 11 x64.");
            return;
        }

        // Wait for Avalonia's transparency negotiation. Opened or a property change retries this wait.
        if (_window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
        {
            _window.Background = _fallback;
            return;
        }

        var handle = _window.TryGetPlatformHandle();
        if (handle is null || handle.HandleDescriptor != "HWND" || handle.Handle == 0)
        {
            SetFailure("The window has no Win32 handle.");
            return;
        }

        var generation = ++_generation;
        // Keep the reverse P/Invoke delegate alive until native hooks have been removed.
        _callback = result => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && generation == _generation)
            {
                SetFailure($"Compositor update failed (0x{result:X8}).");
            }
        });
        try
        {
            var result = NativeMethods.BackdropCreate(handle.Handle, (float)_blurRadius, _callback, out _session);
            if (result < 0)
            {
                SetFailure($"Backdrop initialization failed (0x{result:X8}).");
                return;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException
                                          or BadImageFormatException)
        {
            SetFailure(error.Message);
            return;
        }

        _window.Background = _originalBackground;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetFailure(string reason)
    {
        Stop();
        FailureReason = reason;
        _window.Background = _fallback;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Stop()
    {
        ++_generation;
        if (_session != 0)
        {
            NativeMethods.BackdropDestroy(_session);
            _session = 0;
        }

        _callback = null;
    }
}
