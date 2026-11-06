using System;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>
///     One top-level surface window of <see cref="OverlayController" />, the Quick access sheet or the Game Mode
///     taskbar: the window, the controller navigation it owns, its deferred close and the names its capture and
///     Steam Input claims are made under.
/// </summary>
/// <remarks>
///     The controller makes the claims and runs the post-close work; the slot only holds what one surface owns so
///     both surfaces open, re-show, close and hand over through the same steps. Use on the UI thread.
/// </remarks>
internal sealed class OverlaySurfaceSlot
{
    private IDisposable? _pendingClose;

    /// <summary>Creates an empty slot.</summary>
    /// <param name="surfaceId">The managed-controller capture claim name.</param>
    /// <param name="leaseOwner">The Steam Input lease owner name, unique to this surface.</param>
    internal OverlaySurfaceSlot(string surfaceId, string leaseOwner)
    {
        SurfaceId = surfaceId;
        LeaseOwner = leaseOwner;
    }

    /// <summary>The managed-controller capture claim name.</summary>
    internal string SurfaceId { get; }

    /// <summary>The Steam Input lease owner name.</summary>
    internal string LeaseOwner { get; }

    /// <summary>The open window, including one in its deferred close; null once it has closed.</summary>
    internal Window? Window { get; private set; }

    /// <summary>The window's own controller navigation; each top-level surface window owns one.</summary>
    internal GamepadNavigation? Navigation { get; private set; }

    /// <summary>Whether the window is in its deferred close.</summary>
    internal bool ClosePending => _pendingClose is not null;

    /// <summary>
    ///     Whether another surface took over from this one. Its close then only releases what it held; the
    ///     surface that took over owns the strip, the edges and the focus return.
    /// </summary>
    internal bool HandedOver { get; set; }

    /// <summary>Holds a constructed window, before anything that can fail while it opens.</summary>
    /// <param name="window">The new window.</param>
    internal void Attach(Window window)
    {
        Window = window;
        HandedOver = false;
    }

    /// <summary>Holds the window's controller navigation, which the slot disposes when the window closes.</summary>
    /// <param name="navigation">The navigation over <see cref="Window" />.</param>
    internal void UseNavigation(GamepadNavigation navigation)
    {
        Navigation = navigation;
    }

    /// <summary>Enables the window again and cancels a deferred close in progress.</summary>
    /// <returns>Whether a deferred close was cancelled.</returns>
    internal bool Resume()
    {
        Window?.IsEnabled = true;
        Navigation?.IsEnabled = true;
        if (_pendingClose is null)
        {
            return false;
        }

        _pendingClose.Dispose();
        _pendingClose = null;
        return true;
    }

    /// <summary>
    ///     Disables the window at once and closes it after the touch grace, so a tap's late synthesized click is
    ///     eaten by the still-open window instead of reaching what lies behind it.
    /// </summary>
    /// <returns>Whether a close started; false without a window or when one is already pending.</returns>
    internal bool BeginClose()
    {
        if (Window is null || _pendingClose is not null)
        {
            return false;
        }

        Window.IsEnabled = false;
        Navigation?.IsEnabled = false;
        _pendingClose = DispatcherTimer.RunOnce(() =>
        {
            _pendingClose = null;
            Window?.Close();
        }, TouchInput.CloseGrace);
        return true;
    }

    /// <summary>Forgets the closed window and disposes its navigation and any pending close.</summary>
    /// <returns>Whether the window had been handed over to another surface.</returns>
    internal bool End()
    {
        _pendingClose?.Dispose();
        _pendingClose = null;
        Navigation?.Dispose();
        Navigation = null;
        Window = null;
        var handedOver = HandedOver;
        HandedOver = false;
        return handedOver;
    }
}
