using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Everything the entry transaction and the desktop return can do to the machine. The
///     transaction owns the order and the compensation; the backend owns the effects.
/// </summary>
internal interface IGameModeEntryBackend
{
    /// <summary>Shows one status line on the splash.</summary>
    /// <param name="line">What is happening now.</param>
    void SetStatus(string line);

    /// <summary>Switches the splash button between cancelling entry and giving up on Game Mode.</summary>
    /// <param name="cancellable">True before the boundary, false after it.</param>
    void SetCancellable(bool cancellable);

    /// <summary>Observes the current desktop arrangement.</summary>
    /// <returns>The observation.</returns>
    Task<DisplayArrangement> ObserveAsync();

    /// <summary>Waits until every named monitor is connected and settled. No deadline.</summary>
    /// <param name="targets">Monitors to wait for.</param>
    /// <param name="cancellationToken">The only way this ends other than success.</param>
    /// <returns>The settled observation.</returns>
    Task<DisplayArrangement> WaitForDisplaysAsync(
        IReadOnlyList<DisplayTargetIdentity> targets, CancellationToken cancellationToken);

    /// <summary>Applies a layout using the Windows Device Control result and its bounded rollback policy.</summary>
    /// <remarks>Windows acceptance completes the write; no matching-readback gate or automatic retry is added.</remarks>
    /// <param name="layout">The layout to apply.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <returns>What happened.</returns>
    Task<DisplayLayoutResult> ApplyLayoutAsync(DisplayLayout layout, CancellationToken cancellationToken);

    /// <summary>Applies saved driver display values through freshly discovered output routes.</summary>
    /// <returns>A warning for refused or uncertain values; accepted writes are never retried.</returns>
    Task<string?> ApplyDisplayGpuAsync(IReadOnlyList<DisplayGpuPreference> preferences,
        CancellationToken cancellationToken);

    /// <summary>Applies final HDR state after output color encoding and bit depth have been written.</summary>
    /// <param name="outputs">Active outputs whose requested HDR was deferred during layout activation.</param>
    /// <param name="cancellationToken">Cancels before an HDR write starts; admitted writes are never retried.</param>
    /// <returns>A concise warning for refused HDR writes, or null.</returns>
    Task<string?> ApplyHdrAsync(IReadOnlyList<DisplayLayoutOutput> outputs, CancellationToken cancellationToken);

    /// <summary>Captures readable original values only for driver controls this entry changes.</summary>
    Task<IReadOnlyList<DisplayGpuPreference>> CaptureDisplayGpuAsync(
        IReadOnlyList<DisplayGpuPreference> preferences, CancellationToken cancellationToken,
        bool refreshTopology = false);

    /// <summary>Captures the current desktop audio state for a later return.</summary>
    /// <param name="cancellationToken">Cancels waiting or query admission.</param>
    /// <returns>The readable desktop audio preferences, or null when defaults cannot be read.</returns>
    Task<AudioProfilePreference?> CaptureAudioAsync(CancellationToken cancellationToken);

    /// <summary>Applies optional audio preferences after display layout work.</summary>
    /// <param name="preference">Requested audio settings, or null to preserve all settings.</param>
    /// <param name="cancellationToken">Cancels waiting; in-flight native writes may still complete.</param>
    /// <returns>Per-setting acceptance results, including partial failures; writes are not rolled back.</returns>
    Task<AudioProfileApplyResult> ApplyAudioAsync(
        AudioProfilePreference? preference,
        CancellationToken cancellationToken);

    /// <summary>Records, or clears, the display and audio state this session owes the desktop.</summary>
    /// <param name="layout">The layout to restore later, or null to clear the record.</param>
    /// <param name="audio">The captured audio state to restore later, or null to clear the record.</param>
    /// <param name="graphics">Captured original driver values, or null to clear them.</param>
    /// <returns>A task that completes once the record is on disk.</returns>
    Task PersistPendingReturnAsync(DisplayLayout? layout, AudioProfilePreference? audio,
        IReadOnlyList<DisplayGpuPreference>? graphics = null);

    /// <summary>Applies the scaling posture Default entry uses.</summary>
    /// <returns>Completion of the configured default display posture operation.</returns>
    Task ApplyDefaultPostureAsync();

    /// <summary>Runs the entry actions, stopping at the first failure.</summary>
    /// <param name="steps">The configured entry actions.</param>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>One result per step that ran.</returns>
    Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(
        IReadOnlyList<PluginActionStep> steps, CancellationToken cancellationToken);

    /// <summary>Runs every configured leave action, reporting failures.</summary>
    /// <returns>One result per step.</returns>
    Task<IReadOnlyList<PluginActionStepResult>> RunLeaveActionsAsync();

    /// <summary>
    ///     Puts the desktop layout back when Game Mode ends: the explicit layout, else the layout
    ///     recorded at entry, else the configured Desktop layout, whichever this configuration owes.
    ///     Captured driver values are restored before this layout can disable their outputs.
    /// </summary>
    /// <param name="explicitLayout">A layout the caller captured itself, or null to use the recovery record.</param>
    /// <param name="recovery">The durable recovery record, read once for the whole return.</param>
    /// <returns>A warning when it could not be restored, otherwise null.</returns>
    Task<string?> ApplyReturnLayoutAsync(DisplayLayout? explicitLayout, GameModeLaunchRecovery recovery);

    /// <summary>Applies explicit Desktop driver preferences after the return layout has settled.</summary>
    /// <param name="recovery">The durable recovery record, read once for the whole return.</param>
    /// <returns>A warning for unavailable, refused or uncertain controls; originals remain recorded.</returns>
    Task<string?> ApplyReturnDisplayGpuAsync(GameModeLaunchRecovery recovery);

    /// <summary>Restores desktop audio after the return display layout has settled.</summary>
    /// <param name="recovery">The durable recovery record, read once for the whole return.</param>
    /// <returns>A warning when it could not be restored, otherwise null.</returns>
    Task<string?> ApplyReturnAudioAsync(GameModeLaunchRecovery recovery);

    /// <summary>Captures the Explorer anchor that a later restore needs.</summary>
    /// <returns>Whether the desktop can be safely taken over.</returns>
    Task<bool> PrepareExplorerExitAsync();

    /// <summary>Requests orderly Explorer shell exit and waits for stable absence, bounded.</summary>
    /// <returns>True when taskbar and desktop surfaces are absent; unrelated retired windows may remain.</returns>
    Task<bool> ExitExplorerAndWaitAsync();

    /// <summary>Returns through the shared desktop recovery sequence.</summary>
    /// <param name="layout">Captured layout to restore, or null to use the configured recovery policy.</param>
    /// <param name="runLeaveActions">Whether completed entry actions require the configured leave sequence.</param>
    /// <returns>Whether Explorer was restored; display or audio recovery may remain pending even when true.</returns>
    Task<bool> ReturnToDesktopAsync(DisplayLayout? layout, bool runLeaveActions);

    /// <summary>Arms the splash before requesting Steam, after all open-ended waits.</summary>
    /// <returns>Completion after the splash is ready to observe Steam; this does not establish CEF readiness.</returns>
    Task ArmSteamDetectionAsync();

    /// <summary>Asks Steam for Big Picture once.</summary>
    /// <returns>A warning when it could not be started, otherwise null.</returns>
    Task<string?> RequestBigPictureAsync();

    /// <summary>Brings up the game-mode surfaces and marks the session committed.</summary>
    /// <returns>Completion after game-mode surfaces are activated and session state is committed.</returns>
    Task CommitGameModeAsync();
}
