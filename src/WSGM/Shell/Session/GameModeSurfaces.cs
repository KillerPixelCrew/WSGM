using System;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>
///     Moves the session between its two shells: owns the Game Mode tray host and the desktop tray
///     menu, records the mode, and switches every mode-following service in one shared order on each
///     entry path.
/// </summary>
/// <remarks>
///     The tray host must never coexist with explorer's taskbar (Z-order war over FindWindow, see
///     <see cref="TrayHost" />): gone before explorer starts, back once game mode has closed it. Apps
///     re-home their icons on each side's TaskbarCreated broadcast.
/// </remarks>
/// <param name="state">The session's live state, whose mode this records.</param>
/// <param name="integrations">The plugins told about every mode change.</param>
/// <param name="steamUi">The transport gate, holds and tab sync that follow the mode.</param>
/// <param name="storage">The card services that run only in Game Mode.</param>
/// <param name="system">The volume buttons and launched-window focus that run only in Game Mode.</param>
/// <param name="overlay">Reads the overlay, which the tray host is attached to once it exists.</param>
/// <param name="messageWindow">Reads the process's message window, which holds the shell hook.</param>
internal sealed class GameModeSurfaces(
    SessionState state,
    IntegrationServices integrations,
    SteamUiServices steamUi,
    StorageServices storage,
    SystemServices system,
    Func<OverlayController?> overlay,
    Func<MessageWindow?> messageWindow)
{
    private DesktopTray? _desktopTray;
    private TrayHost? _trayHost;

    /// <summary>Records whether the session is in Game Mode, for everything that follows the mode.</summary>
    /// <param name="inGameMode">Whether the session is in Game Mode.</param>
    internal void SetInGameMode(bool inGameMode)
    {
        state.InGameMode = inGameMode;
        steamUi.PowerMenu?.SetGameMode(inGameMode);
    }

    /// <summary>Creates the desktop tray menu. A failure leaves the session running without it.</summary>
    /// <param name="showOverlay">Opens the overlay.</param>
    /// <param name="enterGameMode">Starts Game Mode.</param>
    /// <param name="openSettings">Opens Settings.</param>
    /// <param name="exit">Exits WSGM.</param>
    internal void StartDesktopTray(Action showOverlay, Action enterGameMode, Action openSettings, Action exit)
    {
        ShellSession.TryStart("desktop tray",
            () => _desktopTray = new DesktopTray(showOverlay, enterGameMode, openSettings, exit));
    }

    /// <summary>
    ///     Creates the game-mode-only surfaces in one shared order: tray host
    ///     first (startup apps' Shell_NotifyIcon registrations need a living
    ///     Shell_TrayWnd, or they only get an icon after the TaskbarCreated-driven retry,
    ///     which message-only tray windows never hear), the shell hook with its volume buttons
    ///     and launched-window focus, then card services. Direct boot and the service takeover
    ///     are separate entry paths from the desktop-to-game transition, so each initial entry
    ///     calls this explicitly.
    /// </summary>
    internal void Enter()
    {
        _ = integrations.NotifyModeAsync(PluginSessionMode.Game);
        _trayHost ??= TrayHost.Create()
                      ?? throw new InvalidOperationException("The Game Mode tray could not be created.");
        overlay()?.AttachTrayHost(_trayHost);
        // One registration for both shell-hook consumers; desktop mode returns it to Explorer.
        if (messageWindow()?.RegisterShellHook() == false)
        {
            Log.Warn("Game Mode shell hook unavailable: volume buttons and launched-window focus are off.");
        }

        system.SetGameModeActive(true);
        storage.ApplyCardServices(true);
    }

    /// <summary>
    ///     Retires Game Mode as the desktop return starts, before Explorer: the actual shell window
    ///     first, then the plugin, card and UI notifications. UI thread.
    /// </summary>
    internal void RetireForDesktop()
    {
        // Retire the actual shell window before optional plugin/card/UI notifications.
        if (_trayHost is not null && !_trayHost.Retire())
        {
            throw new InvalidOperationException("The tray host's native window is still active.");
        }

        _trayHost = null;
        overlay()?.AttachTrayHost(null);
        SetInGameMode(false);
        _desktopTray?.SetDesktop(true);
        _ = integrations.NotifyModeAsync(PluginSessionMode.Desktop);
        steamUi.RequestGateCheck();
        steamUi.TabBootSync.Cancel();
        // Tabs and the badge are game-mode surfaces; the ACF watcher only exists
        // to keep them fresh, so it stands down with them.
        storage.ApplyCardServices(false);
        // The tabs themselves were retracted before Big Picture was asked to close
        // (PrepareForDesktopAsync), and the transport stays held until the return
        // settles. The Wi-Fi indicator and download sort stay: Big Picture on the desktop draws
        // the same header and the same download queue (Claw, 2026-09-11).
        system.SetGameModeActive(false);
        messageWindow()?.DeregisterShellHook();
    }

    /// <summary>Brings Game Mode back once an entry from the desktop committed. UI thread.</summary>
    internal void EnterFromDesktop()
    {
        SetInGameMode(true);
        _desktopTray?.SetDesktop(false);
        steamUi.ReleaseBigPictureHold();
        steamUi.RequestGateCheck();
        Enter();
        // The Steam surfaces need nothing here: the Big Picture restore released above applies
        // them all once, under the master gate.
        // Returning from desktop mode disabled tabs/badge and cancelled the boot
        // sync; re-inject them.
        steamUi.TabBootSync.Kick();
    }

    /// <summary>
    ///     Starts a session that never entered Game Mode on the desktop, with the overlay armed: no
    ///     DesktopModeStarting fires for it, so the mode is cleared here and the card services asked once.
    /// </summary>
    internal void StartOnDesktop()
    {
        _desktopTray?.SetDesktop(true);
        // No DesktopModeStarting fires for a session that never entered game
        // mode, so clear the flag here: the game-mode-only CEF injections must
        // not start next to a live explorer (and nothing would retract them).
        SetInGameMode(false);
        _ = integrations.NotifyModeAsync(PluginSessionMode.Desktop);
        // The third entry path the card services have to be started from. Game-mode boot
        // and the desktop-to-game transition both call this; a session that starts next to a
        // live desktop did not, and since DesktopModeStarting never fires for it either, the
        // volume monitor was simply absent: a card pulled from the reader left its library in
        // Steam's list, with Steam's storage page showing a drive that was not there (Claw,
        // 2026-09-11). The policy decides what runs on the desktop; this only asks it.
        storage.ApplyCardServices(false);
        steamUi.RequestGateCheck();
    }

    /// <summary>Settles the session on a desktop a refused boot takeover preserved.</summary>
    internal void SettleOnPreservedDesktop()
    {
        SetInGameMode(false);
        _ = integrations.NotifyModeAsync(PluginSessionMode.Desktop);
        steamUi.RequestGateCheck();
    }

    /// <summary>Disposes the desktop tray menu.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeDesktopTray(ShutdownSteps steps)
    {
        steps.DisposeOwner("desktop tray", ref _desktopTray);
    }

    /// <summary>Retires the tray host, which Explorer may only replace once it is verifiably gone.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Whether WSGM's taskbar is gone.</returns>
    internal bool RetireTrayHost(ShutdownSteps steps)
    {
        var retired = false;
        steps.Step("Disposing tray host failed", () =>
        {
            if (_trayHost is not null && !_trayHost.Retire())
            {
                throw new InvalidOperationException("The tray host's native window is still active.");
            }

            _trayHost = null;
            retired = true;
        });
        return retired;
    }
}
