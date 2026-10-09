using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Install;
using WSGM.Interop;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    private Task<bool> ShowOnScreenKeyboardAsync(CancellationToken cancellationToken)
    {
        Log.Info($"On-screen keyboard requested: {(_inGameMode ? "Steam" : "Windows")}.");
        return _inGameMode
            ? ToggleSteamSurfaceAsync(SteamNativeSurfaceAction.Keyboard, cancellationToken)
            : RunUiActionAsync(TouchKeyboard.Toggle, cancellationToken);
    }

    /// <summary>Opens Steam's Quick Access, its menu or its keyboard from an OEM or overlay action.</summary>
    /// <remarks>
    ///     As HC does: while the virtual pad is active, Steam's own button is pressed on it and Steam opens
    ///     the surface itself. Without one, Big Picture's shortcut does it. The Game Mode keyboard is
    ///     Steam's native action through CEF. The physical pad is never handed to Steam.
    /// </remarks>
    private async Task<bool> ToggleSteamSurfaceAsync(SteamNativeSurfaceAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action == SteamNativeSurfaceAction.Keyboard)
        {
            if (!_config.Cef.Enabled || _monitor?.IsAlive != true || _steamUiTransport is not { } transport)
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
        if (_deviceCoordinator?.Controllers is { State: ControllerManagementState.Active } controllers
            && await controllers.PressSteamButtonAsync(quickAccess, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await RunUiActionAsync(() => _monitor?.IsAlive == true && Steam.IsBigPictureVisible
                                                                      && Steam.TrySendBigPictureShortcut(
                                                                          quickAccess
                                                                              ? BigPictureShortcut.QuickAccess
                                                                              : BigPictureShortcut.SteamMenu),
            cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     Optional component availability for the already detected exact handheld.
    ///     An integration the user disabled is quiet and opens no device or driver handles here.
    /// </summary>
    private DevicePrerequisiteState ReadDevicePrerequisiteState()
    {
        var device = _deviceCoordinator?.DeviceDefinition;
        if (!_config.DeviceIntegration.Enabled || device is null)
        {
            return new DevicePrerequisiteState(device is not null, _config.DeviceIntegration.Enabled, false, false, []);
        }

        return new DevicePrerequisiteState(
            device is not null,
            _config.DeviceIntegration.Enabled,
            DevicePrerequisiteSource.ControllerLibraryInstalled(AppContext.BaseDirectory),
            DevicePrerequisiteSource.HidHideInstalled(),
            device is null ? [] : SetupComponents.Required(device),
            DevicePrerequisiteSource.HardwareDriverInstalled("PawnIO"),
            DevicePrerequisiteSource.HardwareDriverInstalled("inpoutx64"));
    }

    private async Task EnableDeviceIntegrationAsync()
    {
        await Task.Run(() => _store.Update(fresh =>
        {
            fresh.DeviceIntegration.Enabled = true;
            return true;
        })).ConfigureAwait(false);
        // The live configuration belongs to the UI thread. The banner reads the flag right after the
        // click, so it is set here rather than left to the reload that follows the write.
        await Dispatcher.UIThread.InvokeAsync(() => _config.DeviceIntegration.Enabled = true);
        Log.Info("Device Integration enabled from the overlay's prerequisites banner.");
    }

    /// <summary>
    ///     Every action the running non-device instances declare, for the Settings lists.
    ///     Device instances are excluded: their controls belong to the Device surfaces, and a session
    ///     automation step reaching into hardware policy would be a second owner for it.
    /// </summary>
    private IReadOnlyList<PluginActionOption> ReadPluginActionOptions()
    {
        if (_pluginOverlaySource is not { } source)
        {
            return [];
        }

        PluginInstanceIdentity[] devices =
            [.. source.Device?.Snapshot().Select(instance => instance.Identity) ?? []];
        return
        [
            .. source.Snapshot()
                .Where(instance => !Array.Exists(devices, device => device == instance.Identity)
                                   && instance.Controls is not null)
                .SelectMany(instance => instance.Controls!.Actions.Select(action =>
                    new PluginActionOption(instance.Identity, action,
                        $"{instance.Name} / {instance.Identity.InstanceId}: {action.Label}")))
        ];
    }

    private async Task<bool> RunUiActionAsync(
        Func<bool> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_shutdownRequested)
        {
            return false;
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
            !_shutdownRequested && action());
    }

    private async Task<bool> CyclePerformanceOverlayLevelAsync(
        CancellationToken cancellationToken)
    {
        if (_shutdownRequested || _performanceOverlay is null)
        {
            return false;
        }

        return await _performanceOverlay.CycleOverlayLevelAsync("oem-action", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> CyclePerformanceProfileAsync(CancellationToken cancellationToken)
    {
        if (_deviceCoordinator is not { } coordinator
            || !await coordinator.PowerAssignments.CycleAsync(cancellationToken).ConfigureAwait(false))
        {
            Log.Info("OEM performance-profile cycle skipped: the device offers no power presets right now.");
            return false;
        }

        return true;
    }

    private static async Task ObserveUiCaptureClaimAsync(
        DeviceCoordinator coordinator,
        string surfaceId)
    {
        try
        {
            await coordinator.ClaimUiAsync(surfaceId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Managed controller capture failed for {surfaceId}", ex);
        }
    }
}
