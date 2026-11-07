using System;
using Avalonia.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>
///     The lifecycle the Quick access sheet and the Game Mode taskbar share: the start of an open, show, re-show,
///     deferred close, the release when a window closes and the work after it.
/// </summary>
/// <remarks>
///     The two surfaces never show together. Opening the sheet hands an open taskbar over: the sheet inherits the
///     window to return to and the taskbar's late close only releases its own claims.
/// </remarks>
public sealed partial class OverlayController
{
    private readonly OverlaySurfaceSlot _bar;
    private readonly OverlaySurfaceSlot _sheet;

    /// <summary>The open sheet, including one in its deferred close.</summary>
    private OverlayWindow? Sheet => (OverlayWindow?)_sheet.Window;

    /// <summary>The open taskbar, including one in its deferred close.</summary>
    private TaskbarWindow? Taskbar => (TaskbarWindow?)_bar.Window;

    /// <summary>
    ///     The start every open shares: cancels pending returns, dismisses the boot splash, records the window
    ///     to return to, claims the lease and stops reading the edges.
    /// </summary>
    /// <param name="firstOpen">Whether no window of this surface exists yet, so the return target is new.</param>
    /// <param name="inheritedReturn">A return target handed over by the taskbar, or zero.</param>
    /// <param name="leaseOwner">The Steam Input owner to claim under, or null when this open takes no lease.</param>
    private void PrepareSurfaceOpen(bool firstOpen, nint inheritedReturn, string? leaseOwner)
    {
        _windowReturnCancellation?.Cancel();
        _focusRestoreCancellation?.Cancel();
        _keyboardRequestCancellation?.Cancel();
        // The boot splash dismisses on any WSGM surface: the panel always outranks it.
        OverlayShown?.Invoke();
        if (firstOpen)
        {
            _restoreFocusTo = inheritedReturn != 0 ? inheritedReturn : NativeMethods.GetForegroundWindow();
            _suppressFocusRestore = false;
        }

        if (leaseOwner is not null)
        {
            AcquireSteamInputLease(leaseOwner);
        }

        // The edges would read touches on a docked surface as swipes.
        _activation.Disarm();
    }

    /// <summary>Starts controller navigation and capture for a constructed surface, then shows and focuses it.</summary>
    /// <param name="slot">The surface, whose window is attached.</param>
    /// <param name="navigation">The window's own controller navigation.</param>
    private void ShowSurface(OverlaySurfaceSlot slot, GamepadNavigation navigation)
    {
        var window = slot.Window ?? throw new InvalidOperationException("The surface has no window to show.");
        slot.UseNavigation(navigation);
        _activation.Gamepad.Start();
        ClaimUiSurface(slot.SurfaceId);
        window.Show();
        // Game-Bar-style: the game stops receiving input while the surface is up. Safe because the Steam
        // Input lease keeps the pad readable despite focus.
        window.Activate();
    }

    /// <summary>Brings an open surface back, cancelling its deferred close and restoring its capture claim.</summary>
    /// <param name="slot">The surface, whose window is open.</param>
    private void ResumeSurface(OverlaySurfaceSlot slot)
    {
        if (slot.Resume())
        {
            // The action that requested the close was abandoned with it: a suppressed focus restore must not
            // stay latched for the rest of this surface's life; see the focus-restore finding in
            // docs\overlay-and-input.md.
            _suppressFocusRestore = false;
            Log.Info($"{slot.SurfaceId} re-shown during deferred close; pending close cancelled.");
        }

        if (!_uiSurfaces.Contains(slot.SurfaceId))
        {
            ClaimUiSurface(slot.SurfaceId);
        }
    }

    /// <summary>
    ///     Closes the open surface after the touch grace: the sheet when it is up, otherwise the taskbar.
    /// </summary>
    /// <param name="preserveWindowReturn">Whether an activation queued behind this close keeps running.</param>
    private void CloseSurface(bool preserveWindowReturn)
    {
        if (Sheet is not null)
        {
            CloseOverlay(preserveWindowReturn);
            return;
        }

        if (!preserveWindowReturn)
        {
            _windowReturnCancellation?.Cancel();
        }

        _bar.BeginClose();
    }

    /// <summary>
    ///     Closes the open sheet or taskbar after the display driver restarted under it, so the next summon builds
    ///     a fresh window, backdrop and swapchain on the new driver; the dead one would stay invisible.
    /// </summary>
    /// <returns>Whether a close started: false when nothing is open or the open surface is already closing.</returns>
    internal bool CloseAfterDisplayRestart()
    {
        var slot = _sheet.Window is not null ? _sheet : _bar;
        if (_disposed || slot.Window is null || slot.ClosePending)
        {
            return false;
        }

        Log.Info($"Closing the {slot.SurfaceId} after the display driver restarted.");
        CloseSurface(false);
        return true;
    }

    /// <summary>
    ///     Releases what a closed surface held, then, unless it was handed over or the controller is disposed,
    ///     runs the shared post-close work: the strip closes, the pad stops unless the chord needs it, focus
    ///     returns and the edges are read again.
    /// </summary>
    /// <param name="slot">The surface whose window closed.</param>
    /// <returns>Whether the post-close work ran, so the caller may continue with its own.</returns>
    private bool EndSurface(OverlaySurfaceSlot slot)
    {
        ReleaseUiSurface(slot.SurfaceId);
        _leaseRelease = _steamInput.Drop(slot.LeaseOwner, slot.SurfaceId + "-closed");
        if (slot.End() || _disposed)
        {
            return false;
        }

        // The strip, its rasterized icons and the cached Steam pids go with the surface; the next open re-reads.
        _openApps.Close();
        if (!_activation.ChordActive(_config))
        {
            _activation.Gamepad.Stop();
        }

        RestoreFocusAfterClose();
        _activation.Arm();
        return true;
    }

    /// <summary>Tears down a surface whose open failed after it claimed the lease, as a closed window would be.</summary>
    /// <param name="slot">The surface that failed to open.</param>
    /// <param name="closed">The surface's closed handler.</param>
    private static void FailSurfaceOpen(OverlaySurfaceSlot slot, Action closed)
    {
        var failed = slot.Window;
        closed();
        failed?.Close();
    }

    /// <summary>The sheet or taskbar that is currently open, if any.</summary>
    private Window? OpenSurface()
    {
        return _sheet.Window ?? _bar.Window;
    }

    /// <summary>Whether the sheet or the taskbar shows, while the strip marks the window they opened over.</summary>
    private bool SurfaceShowing()
    {
        return _sheet.Window is { IsVisible: true } || _bar.Window is { IsVisible: true };
    }

    /// <summary>
    ///     Game Mode only: calls back the window focused before the sheet or taskbar opened (exclusive-fullscreen
    ///     games sit minimized by now), unless an action moved focus elsewhere.
    /// </summary>
    private void RestoreFocusAfterClose()
    {
        if (!_suppressFocusRestore && _restoreFocusTo != 0 && !ExplorerControl.IsDesktopShellRunning()
            && GameReturn is { } gameReturn)
        {
            Log.Info("Restoring previously focused window.");
            Log.Observe(RestoreFocusAsync(gameReturn, _restoreFocusTo), "Overlay focus restore");
        }

        _restoreFocusTo = 0;
    }
}
