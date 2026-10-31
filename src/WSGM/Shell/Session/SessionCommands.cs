using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>
///     The session's user commands that cross surfaces: the device's OEM button actions, Steam's
///     Quick Access, menu and keyboard opened from WSGM, the overlay toggled from Steam, and Steam's
///     Switch to Desktop. Every command that touches UI state runs on the UI thread and is refused
///     once shutdown began.
/// </summary>
/// <param name="state">The session's live state.</param>
/// <param name="integrations">The device whose OEM buttons and controllers the commands use.</param>
/// <param name="performance">The performance overlay level an OEM button cycles.</param>
/// <param name="steamUi">The transport Steam's keyboard is opened through.</param>
/// <param name="overlay">Reads the session's overlay.</param>
/// <param name="modes">Reads the session's mode coordinator.</param>
/// <param name="monitor">Reads the session's Steam monitor.</param>
internal sealed class SessionCommands(
    SessionState state,
    IntegrationServices integrations,
    PerformanceServices performance,
    SteamUiServices steamUi,
    Func<OverlayController?> overlay,
    Func<SessionModes?> modes,
    Func<SteamMonitor?> monitor)
{
    /// <summary>Builds what the device's OEM buttons do in this session.</summary>
    /// <returns>The OEM action services.</returns>
    internal DeviceOemActionServices OemActions()
    {
        return new DeviceOemActionServices
        {
            ToggleOverlayAsync = ToggleOverlayAsync,
            ToggleSteamQuickAccessAsync = token =>
                ToggleSteamSurfaceAsync(SteamNativeSurfaceAction.QuickAccess, token),
            ToggleSteamOverlayAsync = token => ToggleSteamSurfaceAsync(SteamNativeSurfaceAction.Home, token),
            WrapperInputLeaseActive = ReadWrapperInputLeaseActive,
            SendSteamShortcutAsync = SendSteamMenuShortcutAsync,
            ToggleDevicePageAsync = cancellationToken => RunUiActionAsync(() =>
            {
                overlay()?.ShowDevicePage();
                return overlay() is not null;
            }, cancellationToken),
            ToggleOpenAppsAsync = cancellationToken => RunUiActionAsync(() =>
            {
                overlay()?.ToggleOpenApps();
                return overlay() is not null;
            }, cancellationToken),
            ToggleDesktopGameModeAsync = cancellationToken => RunUiActionAsync(() =>
            {
                if (modes() is not { } sessionModes)
                {
                    return false;
                }

                if (ExplorerControl.IsDesktopShellRunning())
                {
                    sessionModes.EnterGameMode();
                }
                else
                {
                    sessionModes.EnterDesktopMode();
                }

                return true;
            }, cancellationToken),
            ToggleOnScreenKeyboardAsync = ShowOnScreenKeyboardAsync,
            CyclePerformanceProfileAsync = CyclePerformanceProfileAsync,
            CyclePerformanceOverlayLevelAsync = CyclePerformanceOverlayLevelAsync,
            SetRearButtonAsync = (button, cancellationToken) =>
                integrations.Device?.PulseRearButtonAsync(button, cancellationToken)
                ?? Task.FromResult(false)
        };
    }

    /// <summary>Toggles WSGM's overlay, from an OEM button or from Steam.</summary>
    /// <param name="cancellationToken">Cancels the request before it reaches the UI thread.</param>
    /// <returns>Whether an overlay existed to toggle.</returns>
    internal Task<bool> ToggleOverlayAsync(CancellationToken cancellationToken)
    {
        return RunUiActionAsync(() =>
        {
            overlay()?.ToggleOverlay();
            return overlay() is not null;
        }, cancellationToken);
    }

    /// <summary>Opens Steam's keyboard in Game Mode and the Windows touch keyboard on the desktop.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether a keyboard was toggled.</returns>
    internal Task<bool> ShowOnScreenKeyboardAsync(CancellationToken cancellationToken)
    {
        Log.Info($"On-screen keyboard requested: {(state.InGameMode ? "Steam" : "Windows")}.");
        return state.InGameMode
            ? ToggleSteamSurfaceAsync(SteamNativeSurfaceAction.Keyboard, cancellationToken)
            : RunUiActionAsync(TouchKeyboard.Toggle, cancellationToken);
    }

    /// <summary>Opens Steam's Quick Access, its menu or its keyboard from an OEM or overlay action.</summary>
    /// <remarks>
    ///     A wrapper input lease uses Ctrl+1/Ctrl+2 because Steam cannot read the pad. Otherwise an active
    ///     virtual pad supplies Steam's own button, with Big Picture's shortcut as fallback. The Game Mode keyboard is
    ///     Steam's native action through CEF. The physical pad is never handed to Steam.
    /// </remarks>
    private async Task<bool> ToggleSteamSurfaceAsync(SteamNativeSurfaceAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action == SteamNativeSurfaceAction.Keyboard)
        {
            if (!state.Config.Cef.Enabled || monitor()?.IsAlive != true || steamUi.Transport is not { } transport)
            {
                return false;
            }

            var snapshot = await SteamSideMenuObserver.ReadAsync(transport, cancellationToken)
                .ConfigureAwait(false);
            return SelectReplayTarget(snapshot, Steam.IsBigPictureVisible) is { } target
                   && await SteamNativeSurfaceCommands.ReplayAsync(transport, action, target.ProcessId,
                       target.AppId, snapshot.Generations, cancellationToken).ConfigureAwait(false);
        }

        var quickAccess = action == SteamNativeSurfaceAction.QuickAccess;
        if (ReadWrapperInputLeaseActive())
        {
            return await SendSteamMenuShortcutAsync(quickAccess, cancellationToken).ConfigureAwait(false);
        }

        if (integrations.Device?.Controllers is { State: ControllerManagementState.Active } controllers
            && await controllers.PressSteamButtonAsync(quickAccess, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await RunUiActionAsync(() => monitor()?.IsAlive == true && Steam.IsBigPictureVisible
                                                                      && Steam.TrySendBigPictureShortcut(
                                                                          quickAccess
                                                                              ? BigPictureShortcut.QuickAccess
                                                                              : BigPictureShortcut.SteamMenu),
            cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> SendSteamMenuShortcutAsync(bool quickAccess, CancellationToken cancellationToken)
    {
        return RunUiActionAsync(() => monitor()?.IsAlive == true && Steam.TrySendBigPictureShortcut(
            quickAccess ? BigPictureShortcut.QuickAccess : BigPictureShortcut.SteamMenu), cancellationToken);
    }

    private static bool ReadWrapperInputLeaseActive()
    {
        try
        {
            return WrapperInputLeaseMarker.IsActive();
        }
        catch (Win32Exception ex)
        {
            Log.Change("wrapper-input-lease-marker", $"Wrapper input-lease state unavailable: {ex.Message}",
                LogLevel.Warn);
            return false;
        }
    }

    /// <summary>The Steam window a native surface opens in: the one game overlay, else the main window.</summary>
    /// <param name="snapshot">Steam's side menus as CEF reports them.</param>
    /// <param name="mainVisible">Whether Big Picture's main window is visible.</param>
    /// <returns>The window, or null when there is none or more than one game overlay.</returns>
    internal static SteamWindowSideMenu? SelectReplayTarget(SteamSideMenuSnapshot snapshot, bool mainVisible)
    {
        if (snapshot.Windows is not { Count: > 0 } windows)
        {
            return null;
        }

        var overlays = windows.Where(window => window.ProcessId != 0).ToArray();
        return overlays.Length switch
        {
            0 => mainVisible ? windows[0] : null,
            1 => overlays[0],
            _ => null
        };
    }

    /// <summary>Runs a UI action on the UI thread unless shutdown began first.</summary>
    /// <param name="action">The action; its result says whether it did anything.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the UI thread.</param>
    /// <returns>The action's result, or false once shutdown began.</returns>
    internal async Task<bool> RunUiActionAsync(
        Func<bool> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state.ShutdownRequested)
        {
            return false;
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
            !state.ShutdownRequested && action());
    }

    private async Task<bool> CyclePerformanceOverlayLevelAsync(
        CancellationToken cancellationToken)
    {
        if (state.ShutdownRequested || performance.OverlayBridge is null)
        {
            return false;
        }

        return await performance.OverlayBridge.CycleOverlayLevelAsync("oem-action", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> CyclePerformanceProfileAsync(CancellationToken cancellationToken)
    {
        if (integrations.Device is not { } coordinator
            || !await coordinator.Power.PowerAssignments.CycleAsync(cancellationToken).ConfigureAwait(false))
        {
            Log.Info("OEM performance-profile cycle skipped: the device offers no power presets right now.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Answers Switch to Desktop in Steam's power menu with the transition the overlay's Return to
    ///     Desktop starts. Refused outside Game Mode and while another transition runs.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request before it reaches the UI thread.</param>
    /// <returns>Whether the switch started, or why it was refused.</returns>
    internal async Task<SteamUiCommandResult> SwitchToDesktopFromSteamAsync(CancellationToken cancellationToken)
    {
        var refusal = "WSGM could not start the switch.";
        var started = await RunUiActionAsync(() =>
        {
            if (modes() is not { } sessionModes || !state.InGameMode)
            {
                refusal = "WSGM is not in Game Mode.";
                return false;
            }

            if (sessionModes.TransitionInProgress)
            {
                refusal = "A mode switch is already in progress.";
                return false;
            }

            Log.Info("Switch to Desktop selected in Steam's power menu.");
            sessionModes.EnterDesktopMode();
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return started ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, refusal);
    }
}
