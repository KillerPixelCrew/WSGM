using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>Connects the sheet's page requests to the session actions they start.</summary>
    /// <param name="overlay">The sheet being opened.</param>
    /// <param name="vm">Its view model, which some requests update.</param>
    private void WireOverlayRequests(OverlayWindow overlay, OverlayViewModel vm)
    {
        overlay.PanelPowerSession.HomeAppRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            _modes.SteamLifecycle.StartOrFocus();
        };
        overlay.PanelPowerSession.DesktopRequested += () =>
        {
            // Belt and braces with the hidden row: a preview surface must never run a
            // real transition, and this process has no recovery layer if it did.
            if (_previewOnly)
            {
                Log.Info("Mode switch ignored — this is a preview surface.");
                return;
            }

            // Mid-transition (boot takeover, or a switch already running) the
            // explorer state is in flux — acting on it would start a second,
            // conflicting transition (device-observed 2026-08-07).
            if (_modes.TransitionInProgress)
            {
                Log.Info("Mode switch ignored — an explorer transition is in progress.");
                return;
            }

            var explorerRunning = ExplorerControl.IsDesktopShellRunning();
            _suppressFocusRestore = true;
            CloseOverlay();
            if (explorerRunning)
            {
                _modes.EnterGameMode();
            }
            else
            {
                _modes.EnterDesktopMode();
            }
        };
        overlay.PanelPowerSession.ExitBigPictureRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            SteamLifecycle.ExitBigPicture();
        };
        overlay.PanelPowerSession.CloseLauncherRequested += () =>
        {
            _modes.SteamLifecycle.Close();
            vm.HomeAppAlive = false;
        };
        overlay.PanelPowerActions.PowerActionRequested += action =>
        {
            // The sheet already dismissed itself. A preview (Settings' Test sheet or
            // --overlay-test) demonstrates the row and never acts on the machine, and
            // a controller the session gave no power port (tests) has nothing to call.
            if (_previewOnly || RequestPowerAction is not { } request)
            {
                Log.Info($"Power action {action} ignored — this surface does not act on the machine.");
                return;
            }

            request(action);
        };
        overlay.PanelSystemTools.TaskManagerRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            StartTaskManager();
        };
        overlay.PanelSystemTools.SettingsRequested += () =>
        {
            // A preview sheet hides the row; this is the belt to that brace.
            if (OpenSettings is not { } openSettings)
            {
                return;
            }

            _suppressFocusRestore = true;
            // Managed capture is claimed for Settings before the deferred close below ends the
            // sheet's claim. Only a new claim is released with the window, so a second request for
            // the one Settings window neither claims nor releases twice. The window itself claims
            // the Steam Input lease as it activates, also before that deferred close.
            var claimed = !_uiSurfaces.Contains(SettingsSurface);
            if (claimed)
            {
                ClaimUiSurface(SettingsSurface);
            }

            CloseOverlay();
            // A shell session normally has no main window. Opening settings in this
            // process keeps quick access responsive and avoids starting a second shell.
            Dispatcher.UIThread.Post(() => _ = HandOffAsync());

            async Task HandOffAsync()
            {
                try
                {
                    var settings = await openSettings();
                    if (claimed)
                    {
                        settings.Closed += (_, _) => ReleaseUiSurface(SettingsSurface);
                    }
                }
                catch (Exception ex)
                {
                    if (claimed)
                    {
                        ReleaseUiSurface(SettingsSurface);
                    }

                    Log.Error("Settings handoff window could not open", ex);
                }
            }
        };
        // Native file pickers retain their own navigation while they are visible.
        overlay.SystemDialogActive += active =>
        {
            if (!ReferenceEquals(Sheet, overlay))
            {
                return;
            }

            if (active)
            {
                _dialogPriorNavigation = _sheet.Navigation?.IsEnabled ?? false;
            }

            _sheet.Navigation?.IsEnabled = !active && _dialogPriorNavigation;
        };
        // Dismiss never refocuses anything: Windows hands the foreground back to
        // the previous window on close. An explicit refocus-on-dismiss once yanked
        // Steam over an app the user had deliberately cycled to.
        overlay.Dismissed += CloseOverlay;
        // The notice's text clears through its Changed event, on this sheet and the Steam page alike.
        overlay.SteamBuildNoticeDismissed += () =>
            Log.Observe(Task.Run(() => BuildNotice?.Dismiss()), "Steam client build notice dismissal");
    }
}
