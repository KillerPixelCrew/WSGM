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
    /// <summary>The largest accepted <see cref="BlurRadius" />, in physical pixels.</summary>
    public const double MaximumBlurRadius = 60;

    private static readonly ConditionalWeakTable<Window, LiveBackdrop> Attachments = new();

    // Replaceable native boundary for headless lifecycle checks.
    /// <summary>Replaceable native factory; the returned handle transfers to the attachment.</summary>
    internal static CreateSession Create = NativeMethods.BackdropCreate;
    /// <summary>Replaceable synchronous blur update on the creating UI thread.</summary>
    internal static Func<nint, float, int> SetBlur = NativeMethods.BackdropSetBlur;
    /// <summary>Replaceable native destructor; consumes the handle on its creating UI thread.</summary>
    internal static Action<nint> Destroy = NativeMethods.BackdropDestroy;

    /// <summary>Resolves a borrowed HWND, or zero when the Avalonia platform has none.</summary>
    internal static Func<Window, nint> ReadWindowHandle = static window =>
        window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle ? handle.Handle : 0;

    private readonly IBrush _fallback;
    private readonly IBrush? _originalBackground;
    private readonly Window _window;
    private double _blurRadius;
    private NativeMethods.FailureCallback? _callback;
    private bool _disposed;
    private bool _enabled = true;
    private int _generation;

    private (bool Active, string? Failure, bool Enabled) _published = (false, null, true);
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

    /// <summary>Whether a native session is attached; false while hidden, minimized, disabled or failed.</summary>
    public bool IsActive => _session != 0;

    /// <summary>Gets the last failure, or null when there is no failure. Failed sessions require <see cref="Retry" />.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Gaussian standard deviation in physical pixels, including zero for an unblurred backdrop.</summary>
    /// <value>A finite value from zero through <see cref="MaximumBlurRadius" />; initially 8 unless specified.</value>
    /// <remarks>Updates an active session immediately; otherwise retains the value for the next attachment.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is nonfinite or outside the supported range.</exception>
    /// <exception cref="ObjectDisposedException">The attachment has been disposed.</exception>
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

            var result = SetBlur(_session, (float)value);
            if (result < 0)
            {
                SetFailure($"Blur update failed (0x{result:X8}).");
            }
        }
    }

    /// <summary>Whether a visible, non-minimized window may have an active backdrop.</summary>
    /// <remarks>
    ///     Disabling releases the native session and applies the fallback. Re-enabling does not clear a
    ///     recorded failure; call <see cref="Retry" /> for that. Assigning the current value is a no-op.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The attachment has been disposed.</exception>
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
            // Enabling may stop at a kept failure or the transparency wait, which publish nothing themselves.
            Publish();
        }
    }

    /// <summary>Releases native resources and subscriptions and restores the window's original background and owned style bits.</summary>
    /// <remarks>Call on the UI thread. Repeated disposal is harmless; the window can then receive a new attachment.</remarks>
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

    /// <summary>Raised on the UI thread when active, disabled or failed state changes, and only then.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Attaches a single backdrop owner to a window, before or after it opens.</summary>
    /// <param name="window">The transparent Avalonia window.</param>
    /// <param name="blurRadius">Gaussian standard deviation in physical pixels.</param>
    /// <param name="fallback">Opaque fill used when disabled or unavailable; defaults to dark gray.</param>
    /// <returns>An attachment disposed automatically on close, or explicitly by its owner.</returns>
    /// <remarks>
    ///     Call on the UI thread. Native creation waits for visibility and negotiated transparent composition.
    ///     Missing platform/native capabilities produce fallback state rather than an attachment exception.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="window" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blurRadius" /> is nonfinite or outside 0–60.</exception>
    /// <exception cref="InvalidOperationException">The caller is off the UI thread or the window already has an attachment.</exception>
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

    /// <summary>Clears the recorded failure and recreates the session when visibility, enablement and transparency allow.</summary>
    /// <remarks>Call on the UI thread. Also restarts a healthy session; hidden or disabled windows remain inactive.</remarks>
    /// <exception cref="ObjectDisposedException">The attachment has been disposed.</exception>
    public void Retry()
    {
        VerifyAccess();
        Stop();
        FailureReason = null;
        Reconcile();
        Publish();
    }

    private static void ValidateBlur(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > MaximumBlurRadius)
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
            Publish();
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

        var handle = ReadWindowHandle(_window);
        if (handle == 0)
        {
            SetFailure("The window has no Win32 handle.");
            return;
        }

        var generation = ++_generation;
        // Root the callback until native hooks are removed; the generation rejects queued failures after Stop.
        _callback = result => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && generation == _generation)
            {
                SetFailure($"Compositor update failed (0x{result:X8}).");
            }
        });
        try
        {
            var result = Create(handle, (float)_blurRadius, _callback, out _session);
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
        Publish();
    }

    private void SetFailure(string reason)
    {
        Stop();
        FailureReason = reason;
        _window.Background = _fallback;
        Publish();
    }

    private void Publish()
    {
        var current = (IsActive, FailureReason, _enabled);
        if (current == _published)
        {
            return;
        }

        _published = current;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Stop()
    {
        ++_generation;
        if (_session != 0)
        {
            Destroy(_session);
            _session = 0;
        }

        _callback = null;
    }

    /// <summary>Replaceable native session factory with the same ownership contract as <see cref="NativeMethods.BackdropCreate" />.</summary>
    /// <param name="owner">Borrowed HWND on the calling UI thread.</param>
    /// <param name="sigma">Finite Gaussian deviation in physical pixels, from zero through 60.</param>
    /// <param name="callback">Rooted until destruction; queues failure handling without destroying the session inline.</param>
    /// <param name="session">Owned handle on success; zero on failure.</param>
    /// <returns>An HRESULT; a negative value indicates failure.</returns>
    internal delegate int CreateSession(nint owner, float sigma, NativeMethods.FailureCallback callback,
        out nint session);
}
