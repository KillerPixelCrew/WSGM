using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>The Game Mode taskbar: its open, re-show, hand-over to the sheet and its own actions.</summary>
/// <remarks>The lifecycle it shares with the sheet lives in <see cref="OverlaySurfaceSlot" /> and the Surfaces partial.</remarks>
public sealed partial class OverlayController
{
    private const string TaskbarSurface = "taskbar";

    /// <summary>Whether the Game Mode taskbar is open.</summary>
    internal bool TaskbarOpen => Taskbar is not null;

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

        if (Sheet is not null)
        {
            Log.Info("Taskbar not opened: the Quick access sheet is up.");
            return;
        }

        if (_bar.HandedOver)
        {
            // A handed-over taskbar still in its grace belongs to a sheet that has since closed: finish it now
            // and open a fresh one rather than reviving its stale strip.
            Taskbar?.Close();
        }

        if (Taskbar is { } open)
        {
            ResumeSurface(_bar);
            _openApps.Refresh();
            open.Activate();
            return;
        }

        try
        {
            OpenTaskbar();
        }
        catch
        {
            FailSurfaceOpen(_bar, OnTaskbarClosed);
            throw;
        }
    }

    /// <summary>Opens the taskbar, or closes it when it is up.</summary>
    public void ToggleTaskbar()
    {
        if (Taskbar is null)
        {
            ShowTaskbar();
        }
        else
        {
            CloseSurface(false);
        }
    }

    private void OpenTaskbar()
    {
        PrepareSurfaceOpen(true, 0, _bar.LeaseOwner);
        var taskbar = new TaskbarWindow(_openApps.Open(), UiScale(ExplorerControl.IsDesktopShellRunning()),
            WindowCenter(_restoreFocusTo));
        _bar.Attach(taskbar);
        taskbar.AttachPrograms(_services.Sources.Programs);
        taskbar.WindowPicked += PickWindow;
        taskbar.WindowCloseRequested += CloseAppWindow;
        taskbar.TrayIconActivated += OnTrayIconActivated;
        taskbar.ProgramChosen += ActivateProgram;
        taskbar.ManageProgramsRequested += () => OpenProgramsPage(null);
        taskbar.DismissRequested += () => CloseSurface(false);
        taskbar.Closed += (_, _) =>
        {
            if (ReferenceEquals(Taskbar, taskbar))
            {
                OnTaskbarClosed();
            }
        };

        ShowSurface(_bar, new GamepadNavigation(_activation.Gamepad, taskbar, taskbar.Back, IsNintendoLayout,
            () => taskbar.DefaultFocusTarget, taskbar.RequestSecondaryAction,
            focusScope: () => taskbar.NavigationRoot));
        _openApps.Start();
        Log.Info("Game Mode taskbar shown.");
    }

    /// <summary>Starts closing an open taskbar because the sheet is opening in its place.</summary>
    /// <returns>The window the taskbar opened over, which the sheet returns to; zero without a taskbar.</returns>
    private nint HandTaskbarToOverlay()
    {
        if (Taskbar is null)
        {
            return 0;
        }

        Log.Info("Taskbar handed over to the Quick access sheet.");
        _bar.HandedOver = true;
        _bar.BeginClose();
        return _restoreFocusTo;
    }

    private void OnTaskbarClosed()
    {
        EndSurface(_bar);
    }

    /// <summary>Closes the taskbar at once as the controller is disposed; its closed handler releases its claims.</summary>
    private void DisposeTaskbar()
    {
        Taskbar?.Close();
    }

    /// <summary>
    ///     Asks a listed window to close, as its own close button would. The taskbar stays open so several can be
    ///     closed; the strip drops the window on its next refresh once the application has gone.
    /// </summary>
    private static void CloseAppWindow(AppSwitcherEntry entry)
    {
        if (entry.IsSteam)
        {
            Log.Info($"Taskbar: not closing Steam's window '{entry.Title}'.");
            return;
        }

        Log.Info(DesktopWindows.RequestClose(entry.Hwnd, entry.ProcessId)
            ? $"Taskbar: asked '{entry.Title}' to close."
            : $"Taskbar: '{entry.Title}' is no longer the listed window; nothing closed.");
    }

    /// <summary>
    ///     Opens the sheet's Programs page, in place of the taskbar when it is up, on the editor of a program that
    ///     cannot launch when one is given.
    /// </summary>
    private void OpenProgramsPage(string? editorId)
    {
        if (_disposed)
        {
            return;
        }

        // An open sheet stays where it is; re-summoning it would reset its navigation.
        if (Sheet is null)
        {
            ShowOverlay();
        }

        Sheet?.OpenPrograms(editorId);
    }
}
