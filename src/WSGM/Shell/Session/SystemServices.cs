using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     The session's Windows-facing services, built and disposed on the UI thread: audio and radios
///     for Steam's surfaces and the sheet's pills, the keep-awake and screen-off mute, brightness, the
///     Modern Standby guard, the controller display wake, the update monitor, the Game Mode volume
///     buttons and launched-window focus, and the guide chord mirror that follows the managed controller.
/// </summary>
/// <param name="state">The session's live state.</param>
internal sealed class SystemServices(SessionState state)
{
    private SteamGuideChordMirror? _chordMirror;
    private ControllerManager? _controllerStatusSource;
    private ControllerDisplayWake? _controllerWake;

    // Field-rooted for the session lifetime: it owns a native power-setting
    // registration and the "did WSGM mute this?" flag.
    private DisplayOffMuteService? _displayMute;

    private LaunchedWindowFocus? _launchedWindows;
    private ModernStandbyGuard? _standbyGuard;
    private UpdateMonitor? _updates;
    private VolumeButtonService? _volumeButtons;

    /// <summary>
    ///     The one audio manager for this session, shared by the quick access sheet's status pills and
    ///     Steam's audio namespace.
    /// </summary>
    /// <remarks>
    ///     Session-scoped because the sheet is not: it comes and goes, and Steam's audio store has to
    ///     answer for the whole session. A second manager would enumerate endpoints twice and could
    ///     disagree with the sheet about which device is default.
    /// </remarks>
    internal AudioManager? Audio { get; private set; }

    /// <summary>Serializes profile and live advanced-format writes against the session audio manager.</summary>
    internal AudioProfileService? AudioProfiles { get; private set; }

    /// <summary>
    ///     The one radio manager for this session, shared by the quick access sheet's status pills and
    ///     Steam's network surface.
    /// </summary>
    /// <remarks>
    ///     Session-scoped for the same reason as the audio manager, and idle by default: scanning costs
    ///     power and only makes sense while a network list is on screen.
    /// </remarks>
    internal RadioManager? Radios { get; private set; }

    /// <summary>The download and manual keep-awake, for both modes.</summary>
    internal KeepAwakeService? KeepAwake { get; private set; }

    /// <summary>The brightness Steam's Quick Access and the overlay share; null in overlay-test.</summary>
    internal NativeQamBrightnessService? Brightness { get; private set; }

    /// <summary>The guide chord mirror, which Steam's Quick Access offers.</summary>
    internal SteamGuideChordMirror? ChordMirror => _chordMirror;

    /// <summary>
    ///     Starts the keep-awake. Session-lifetime on purpose (survives desktop trips): a Steam download
    ///     must keep the device awake in both modes, and the manual hold belongs to the user.
    /// </summary>
    /// <param name="monitor">The Steam monitor the download poll follows.</param>
    /// <param name="steamUi">The readiness and client the download poll asks Steam through.</param>
    /// <remarks>
    ///     The automatic side is off in overlay-test mode: its poll drives the live Steam client over CEF
    ///     (and would write the debug flag into a Steam install that never opted in), which the safe local
    ///     modes must not do. The manual toggle still works there — it only takes a local power request.
    /// </remarks>
    internal void StartKeepAwake(SteamMonitor monitor, SteamUiServices steamUi)
    {
        ShellSession.TryStart("keep awake", () =>
        {
            KeepAwake = KeepAwakeService.StartNew(
                monitor,
                AutoKeepAwakeEnabled(state.Config),
                DownloadMonitoringEnabled(state.Config),
                () => !state.InGameMode || steamUi.Readiness.IsReady,
                steamUi.Client);
            KeepAwake.DownloadActivityChanged += OnDownloadActivityChanged;
        });
    }

    /// <summary>
    ///     Starts the audio services, the guide chord mirror and the idle radio manager. Not in
    ///     overlay-test, where the sheet's status pills own their own managers because no Steam surface
    ///     exists there to serve.
    /// </summary>
    internal void StartAudioAndRadios()
    {
        ShellSession.TryStart("audio services", () =>
        {
            Audio = new AudioManager();
            ShellSession.TryStart("audio manager", Audio.Start);
            AudioProfiles = new AudioProfileService(new CoreAudioProfileOperations(), Audio);
        });
        // Follows the managed controller target: it only has work while a Steam Deck type target
        // exists, and it puts Valve's file back when that target goes.
        ShellSession.TryStart("guide chord mirror", () =>
        {
            _chordMirror = SteamGuideChordMirror.ForInstalledSteam();
            _chordMirror?.SetEnabled(state.Config.DeviceIntegration.KeepGuideChordEdits);
        });

        // Not started here: scanning is expensive and belongs to whichever surface is showing a
        // network list. The manager exists for the whole session so Steam's Internet page can
        // drive it, but it stays idle until something asks.
        ShellSession.TryStart("radio manager", () => Radios = new RadioManager());
    }

    /// <summary>Creates the brightness service Steam's Quick Access and the overlay share.</summary>
    internal void StartBrightness()
    {
        ShellSession.TryStart("brightness service",
            () => Brightness = new NativeQamBrightnessService(() => !state.ShutdownRequested));
    }

    /// <summary>Lets the guide chord mirror follow the managed controller target.</summary>
    /// <param name="controllers">The device's controller manager.</param>
    internal void FollowControllers(ControllerManager controllers)
    {
        if (_controllerStatusSource is not null)
        {
            _controllerStatusSource.StatusChanged -= OnControllerStatusChanged;
        }

        _controllerStatusSource = controllers;
        _controllerStatusSource.StatusChanged += OnControllerStatusChanged;
    }

    /// <summary>Lets the guide chord mirror follow the managed target.</summary>
    /// <param name="status">The new controller projection.</param>
    /// <remarks>
    ///     Raised on any thread, so it touches no session or UI state: the mirror keeps both of its
    ///     inputs under its own lock. Steam only reloads its chord template for a Steam Deck type
    ///     controller, and the mirror restores Valve's file otherwise.
    /// </remarks>
    private void OnControllerStatusChanged(ControllerManagerStatus status)
    {
        _chordMirror?.SetSteamDeckTargetActive(status is
        {
            State: ControllerManagementState.Active,
            Target: ManagedControllerTarget.SteamDeckComposite
        });
    }

    /// <summary>
    ///     Starts the message-window services of the real shell: volume buttons, launched-window focus,
    ///     the Modern Standby guard, the controller display wake, the update monitor and the screen-off
    ///     mute.
    /// </summary>
    /// <param name="messageWindow">The process's message window.</param>
    /// <param name="performance">The running application launched-window focus follows.</param>
    /// <param name="device">The device whose controllers wake the display, or null.</param>
    internal void StartShellServices(MessageWindow messageWindow, PerformanceServices performance,
        DeviceCoordinator? device)
    {
        var downloadActive = KeepAwake?.DownloadActive ?? false;
        ShellSession.TryStart("volume buttons", () => _volumeButtons = new VolumeButtonService(messageWindow, Audio!));
        ShellSession.TryStart("launched window focus", () => _launchedWindows = new LaunchedWindowFocus(messageWindow,
            () => performance.RunningApplications?.Current is { } target
                  && (target.SteamAppId is not null || target.State == RunningApplicationTargetState.Ambiguous)));
        // Reads the flag on every look rather than capturing it, so a runtime config reload takes
        // effect without the guard being rebuilt; the config is replaced wholesale on reload.
        ShellSession.TryStart("modern standby guard", () =>
            _standbyGuard = new ModernStandbyGuard(messageWindow, () => state.Config.ResuspendUnexplainedWakes));
        if (device is { } wakeCoordinator)
        {
            ShellSession.TryStart("controller display wake",
                () => _controllerWake = new ControllerDisplayWake(messageWindow, wakeCoordinator.Controllers));
        }

        // Reads the flag on every wakeup, like the standby guard, so a config reload needs no rebuild.
        ShellSession.TryStart("update monitor",
            () => _updates = new UpdateMonitor(state.Store.Context, () => state.Config.CheckForUpdates));
        ShellSession.TryStart("screen-off mute", () =>
        {
            var displayMute = new DisplayOffMuteService(messageWindow);
            _displayMute = displayMute;
            displayMute.ApplyConfig(state.Config.MuteWhileDisplayOff);
            displayMute.SetDownloadActive(downloadActive);
        });
        if (state.Config is { MuteWhileDisplayOff: true, Cef.Enabled: false })
        {
            // The mute only engages while Steam reports a download, and that comes
            // from the CEF poll. An upgraded config can carry MuteWhileDisplayOff
            // true with Steam integration off, where every log line lives inside the
            // poll that never runs — so say it once here, or a pasted log shows
            // nothing at all for a feature the user can see switched on.
            Log.Warn(
                "Mute screen-off downloads is enabled but Steam integration is off; "
                + "download state is unavailable, so muting will never engage.");
        }
    }

    /// <summary>Turns the Game Mode volume buttons and launched-window focus on or off with the mode.</summary>
    /// <param name="active">Whether Game Mode is active.</param>
    internal void SetGameModeActive(bool active)
    {
        _volumeButtons?.SetGameModeActive(active);
        _launchedWindows?.SetGameModeActive(active);
    }

    /// <summary>Applies a reloaded screen-off mute switch.</summary>
    /// <param name="config">The reloaded configuration.</param>
    internal void ApplyMuteConfig(AppConfig config)
    {
        _displayMute?.ApplyConfig(config.MuteWhileDisplayOff);
    }

    /// <summary>Applies a reloaded guide chord switch.</summary>
    /// <param name="config">The reloaded configuration.</param>
    internal void ApplyChordConfig(AppConfig config)
    {
        _chordMirror?.SetEnabled(config.DeviceIntegration.KeepGuideChordEdits);
    }

    /// <summary>Applies the reloaded keep-awake gates.</summary>
    /// <param name="config">The reloaded configuration.</param>
    internal void ApplyKeepAwakeConfig(AppConfig config)
    {
        KeepAwake?.ApplyConfig(
            AutoKeepAwakeEnabled(config),
            DownloadMonitoringEnabled(config));
    }

    /// <summary>
    ///     Whether the automatic download wake lock may poll Steam: its CEF
    ///     query is autonomous Steam traffic, so it stays off in overlay-test mode
    ///     alongside the other injections that mode excludes.
    /// </summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool AutoKeepAwakeEnabled(AppConfig config)
    {
        return !state.OverlayTestOnly && config.Cef is { Enabled: true, DownloadKeepAwake: true };
    }

    /// <summary>
    ///     Whether the shared Steam download poll has at least one consumer.
    ///     The mute feature reuses the same answer even when its automatic wake lock is
    ///     disabled; overlay-test still excludes all autonomous Steam traffic.
    /// </summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool DownloadMonitoringEnabled(AppConfig config)
    {
        return !state.OverlayTestOnly
               && config.Cef.Enabled
               && (config.Cef.DownloadKeepAwake || config.MuteWhileDisplayOff);
    }

    /// <summary>
    ///     Marshals the shared poller's download transition onto the UI thread,
    ///     where the display mute service and its timers are owned.
    /// </summary>
    /// <param name="active">Whether Steam reports an active download.</param>
    private void OnDownloadActivityChanged(bool active)
    {
        Dispatcher.UIThread.Post(() => _displayMute?.SetDownloadActive(active));
    }

    /// <summary>Disposes the brightness service.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeBrightness(ShutdownSteps steps)
    {
        steps.Step("Disposing the brightness service during application shutdown failed",
            () => Brightness?.Dispose());
    }

    /// <summary>Stops following the managed controller, before the device stops.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DetachControllers(ShutdownSteps steps)
    {
        steps.Step("Detaching controller status failed", () =>
        {
            if (_controllerStatusSource is not null)
            {
                _controllerStatusSource.StatusChanged -= OnControllerStatusChanged;
                _controllerStatusSource = null;
            }
        });
    }

    /// <summary>
    ///     Disposes the message-window services on the UI thread that built them: the controller display
    ///     wake, the Modern Standby guard, the screen-off mute, the update monitor, the volume buttons and
    ///     launched-window focus.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeShellServices(ShutdownSteps steps)
    {
        steps.DisposeOwner("controller display wake", ref _controllerWake);
        steps.DisposeOwner("Modern Standby guard", ref _standbyGuard);
        steps.DisposeOwner("screen-off mute", ref _displayMute);
        steps.DisposeOwner("update monitor", ref _updates);
        steps.DisposeOwner("volume buttons", ref _volumeButtons);
        steps.DisposeOwner("launched-window focus", ref _launchedWindows);
    }

    /// <summary>Disposes the keep-awake, before the Steam monitor it follows.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeKeepAwake(ShutdownSteps steps)
    {
        steps.Step("Disposing keep awake failed", () =>
        {
            if (KeepAwake is not null)
            {
                KeepAwake.DownloadActivityChanged -= OnDownloadActivityChanged;
                KeepAwake.Dispose();
                KeepAwake = null;
            }
        });
    }

    /// <summary>
    ///     Disposes the chord mirror, after the Steam host and the overlay that both hold it, then the audio
    ///     services, the volume feedback and the radios. Valve's chord template goes back when WSGM leaves;
    ///     the next start mirrors again.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task StopAudioAndRadiosAsync(ShutdownSteps steps)
    {
        await steps.UiStepAsync("Disposing the chord mirror during application shutdown failed", () =>
        {
            _chordMirror?.Dispose();
            _chordMirror = null;
        }).ConfigureAwait(false);
        await steps.StepAsync("Disposing the audio profile service during application shutdown failed",
            async () =>
            {
                var audioProfiles = AudioProfiles;
                AudioProfiles = null;
                if (audioProfiles is not null)
                {
                    await audioProfiles.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        await steps.UiStepAsync("Disposing the audio manager during application shutdown failed", () =>
        {
            Audio?.Dispose();
            Audio = null;
        }).ConfigureAwait(false);
        await steps.StepAsync("Disposing the volume feedback during application shutdown failed",
            () => Task.Run(VolumeFeedback.Dispose)).ConfigureAwait(false);
        await steps.UiStepAsync("Disposing the radio manager during application shutdown failed", () =>
        {
            Radios?.Dispose();
            Radios = null;
        }).ConfigureAwait(false);
    }
}
