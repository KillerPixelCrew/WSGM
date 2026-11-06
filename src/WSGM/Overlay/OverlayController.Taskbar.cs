using System;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Input;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>
///     The Game Mode taskbar's lifetime: open, the hand-over to the sheet, deferred close and the paired lease,
///     capture, navigation and focus transitions.
/// </summary>
/// <remarks>
///     The taskbar and the sheet never show together. Opening the sheet replaces the taskbar and inherits the
///     window to return to; the taskbar claims the Steam Input lease under its own owner, so the hand-over keeps
///     the lease held throughout.
/// </remarks>
public sealed partial class OverlayController
{
    private const string TaskbarSurface = "taskbar";

    /// <summary>The taskbar's own name in the blocker's ownership set; see <see cref="_leaseOwner" />.</summary>
    private readonly string _taskbarLeaseOwner = SteamInputBlocker.NewOwner("overlay-taskbar");

    private IDisposable? _pendingTaskbarClose;
    private TaskbarWindow? _taskbar;
    private GamepadNavigation? _taskbarNavigation;

    /// <summary>Whether the Game Mode taskbar is open.</summary>
    internal bool TaskbarOpen => _taskbar is not null;

    /// <summary>
    ///     Opens the Game Mode taskbar on the bottom edge, or brings it back when it is closing. The sheet
    ///     outranks it: while the sheet is up the request is ignored.
    /// </summary>
    public void ShowTaskbar()
    {
        if (_disposed)
        {
            return;
        }

        if (_overlay is not null)
        {
            Log.Info("Taskbar not opened: the Quick access sheet is up.");
            return;
        }

        if (_taskbar is not null)
        {
            ReactivateTaskbar(_taskbar);
            return;
        }

        try
        {
            OpenTaskbar();
        }
        catch
        {
            // Every step after the lease claim needs the same teardown as a failed native show.
            var failed = _taskbar;
            OnTaskbarClosed();
            failed?.Close();
            throw;
        }
    }

    /// <summary>Opens the taskbar, or closes it when it is up.</summary>
    public void ToggleTaskbar()
    {
        if (_taskbar is null)
        {
            ShowTaskbar();
        }
        else
        {
            CloseTaskbar();
        }
    }

    private void OpenTaskbar()
    {
        _windowReturnCancellation?.Cancel();
        _focusRestoreCancellation?.Cancel();
        _keyboardRequestCancellation?.Cancel();
        // The boot splash dismisses on any WSGM surface, as it does for the sheet.
        OverlayShown?.Invoke();
        _restoreFocusTo = NativeMethods.GetForegroundWindow();
        _suppressFocusRestore = false;
        AcquireSteamInputLease(_taskbarLeaseOwner);
        // The strip sits on the bottom edge: a re-armed edge would read touches on it as swipes.
        _activation.Disarm();

        var switcher = _openApps.Open();
        var taskbar = new TaskbarWindow(switcher, UiScale(ExplorerControl.IsDesktopShellRunning()),
            WindowCenter(_restoreFocusTo));
        _taskbar = taskbar;
        taskbar.AttachPrograms(_services.Sources.Programs);
        taskbar.WindowPicked += PickWindow;
        taskbar.WindowCloseRequested += CloseAppWindow;
        taskbar.TrayIconActivated += OnTrayIconActivated;
        taskbar.ProgramChosen += ActivateProgram;
        taskbar.ManageProgramsRequested += OpenProgramsPage;
        taskbar.DismissRequested += CloseTaskbar;
        taskbar.Closed += (_, _) =>
        {
            if (ReferenceEquals(_taskbar, taskbar))
            {
                OnTaskbarClosed();
            }
        };

        _taskbarNavigation = new GamepadNavigation(_activation.Gamepad, taskbar, taskbar.Back, IsNintendoLayout,
            () => taskbar.DefaultFocusTarget, taskbar.RequestSecondaryAction,
            focusScope: () => taskbar.NavigationRoot);
        _activation.Gamepad.Start();
        ClaimUiSurface(TaskbarSurface);
        taskbar.Show();
        // Like the sheet, the taskbar takes focus; the Steam Input lease keeps the pad readable.
        taskbar.Activate();
        _openApps.Start();
        Log.Info("Game Mode taskbar shown.");
    }

    private void ReactivateTaskbar(TaskbarWindow taskbar)
    {
        taskbar.IsEnabled = true;
        _taskbarNavigation?.IsEnabled = true;
        if (_pendingTaskbarClose is not null)
        {
            // Re-summoned inside the deferred close: keep the window and its lease.
            _pendingTaskbarClose.Dispose();
            _pendingTaskbarClose = null;
            _suppressFocusRestore = false;
            Log.Info("Taskbar re-shown during deferred close — pending close cancelled.");
        }

        _windowReturnCancellation?.Cancel();
        _openApps.Refresh();
        if (!_uiSurfaces.Contains(TaskbarSurface))
        {
            ClaimUiSurface(TaskbarSurface);
        }

        taskbar.Activate();
    }

    private void CloseTaskbar()
    {
        CloseTaskbar(false);
    }

    /// <summary>
    ///     Closes the taskbar after the same 150 ms touch grace as the sheet, so a tap's synthesized click is
    ///     eaten by the still-open window instead of reaching the game.
    /// </summary>
    /// <param name="preserveWindowReturn">Whether an activation queued behind this close keeps running.</param>
    private void CloseTaskbar(bool preserveWindowReturn)
    {
        if (!preserveWindowReturn)
        {
            _windowReturnCancellation?.Cancel();
        }

        if (_taskbar is null || _pendingTaskbarClose is not null)
        {
            return;
        }

        _taskbar.IsEnabled = false;
        _taskbarNavigation?.IsEnabled = false;
        _pendingTaskbarClose = DispatcherTimer.RunOnce(() =>
        {
            _pendingTaskbarClose = null;
            _taskbar?.Close();
        }, TouchInput.CloseGrace);
    }

    /// <summary>
    ///     Starts closing an open taskbar because the sheet is opening in its place.
    /// </summary>
    /// <returns>The window the taskbar opened over, which the sheet returns to; zero without a taskbar.</returns>
    private nint HandTaskbarToOverlay()
    {
        if (_taskbar is null)
        {
            return 0;
        }

        var restore = _restoreFocusTo;
        Log.Info("Taskbar handed over to the Quick access sheet.");
        CloseTaskbar(true);
        return restore;
    }

    /// <summary>Releases everything the taskbar held once its window has closed.</summary>
    private void OnTaskbarClosed()
    {
        ReleaseUiSurface(TaskbarSurface);
        _pendingTaskbarClose?.Dispose();
        _pendingTaskbarClose = null;
        _taskbarNavigation?.Dispose();
        _taskbarNavigation = null;
        // The sheet, when it took over, holds the lease under its own owner, so this never drops it under it.
        _leaseRelease = _steamInput.Drop(_taskbarLeaseOwner, "taskbar-closed");
        _taskbar = null;
        if (_overlay is not null)
        {
            // Handed over: the sheet now owns the strip, the pad, the edges and the return.
            return;
        }

        _openApps.Close();
        if (!_activation.ChordActive(_config))
        {
            _activation.Gamepad.Stop();
        }

        RestoreFocusAfterClose();
        _activation.Arm();
    }

    /// <summary>Ends the taskbar's claims at once as the controller is disposed, then closes it.</summary>
    private void DisposeTaskbar()
    {
        if (_taskbar is null)
        {
            return;
        }

        _taskbar.AttachPrograms(null);
        ReleaseUiSurface(TaskbarSurface);
        _steamInput.Drop(_taskbarLeaseOwner, "taskbar-disposed");
        CloseTaskbar();
    }

    /// <summary>
    ///     Asks a listed window to close, as its own close button would. The taskbar stays open so several can be
    ///     closed; the strip drops the window on its next refresh once the application has gone.
    /// </summary>
    private void CloseAppWindow(AppSwitcherEntry entry)
    {
        if (entry.IsSteam)
        {
            Log.Info($"Taskbar: not closing Steam's window '{entry.Title}'.");
            return;
        }

        Log.Info(WindowFinder.RequestClose(entry.Hwnd, entry.ProcessId)
            ? $"Taskbar: asked '{entry.Title}' to close."
            : $"Taskbar: '{entry.Title}' is no longer the listed window; nothing closed.");
        _openApps.Refresh();
    }

    /// <summary>
    ///     Opens the sheet's Programs page in place of the taskbar, on the editor of a program that cannot launch
    ///     when one is given.
    /// </summary>
    private void OpenProgramsPage(string? editorId)
    {
        if (_disposed)
        {
            return;
        }

        ShowOverlay();
        _overlay?.OpenPrograms(editorId);
    }

    /// <summary>The sheet or taskbar that is currently open, if any.</summary>
    private Window? OpenSurface()
    {
        return (Window?)_overlay ?? _taskbar;
    }

    /// <summary>Whether the sheet or the taskbar shows, while the strip marks the window they opened over.</summary>
    private bool SurfaceShowing()
    {
        return _overlay is { IsVisible: true } || _taskbar is { IsVisible: true };
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
