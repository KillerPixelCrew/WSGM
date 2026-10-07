using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>How an attempt to enter Game Mode ended.</summary>
internal enum GameModeEntryOutcome
{
    /// <summary>Game Mode is running.</summary>
    Entered,

    /// <summary>The user cancelled and desktop recovery was attempted; recovery may remain pending.</summary>
    Cancelled,

    /// <summary>Entry failed. The desktop either never changed or went through the desktop return.</summary>
    Failed,

    /// <summary>Explorer could not be removed, so the desktop was deliberately kept.</summary>
    DesktopPreserved
}

/// <summary>The result of one entry attempt.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Warning">
///     User-facing warning, or null when there is nothing to say. Also null when the desktop return
///     failed, because the return has already shown the pending-desktop warning.
/// </param>
internal sealed record GameModeEntryResult(GameModeEntryOutcome Outcome, string? Warning = null);

/// <summary>
///     Everything the entry transaction and the desktop return can do to the machine. The
///     transaction owns the order and the compensation; the backend owns the effects.
/// </summary>
internal interface IGameModeEntryBackend
{
    /// <summary>
    ///     Restores a desktop state an earlier session still owes, and clears its record once
    ///     restored. A refusal leaves the desktop untouched and lets Steam be watched again.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the restore.</param>
    /// <returns>Whether nothing is owed any more.</returns>
    Task<bool> RestorePendingReturnAsync(CancellationToken cancellationToken);

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
    /// <returns>A task that completes once the record is on disk.</returns>
    Task PersistPendingReturnAsync(DisplayLayout? layout, AudioProfilePreference? audio);

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
    ///     Puts the desktop layout back when Game Mode ends: the layout recorded at entry, or the
    ///     configured Desktop layout, whichever this configuration owes.
    /// </summary>
    /// <returns>A warning when it could not be restored, otherwise null.</returns>
    Task<string?> ApplyReturnLayoutAsync();

    /// <summary>Restores desktop audio after the return display layout has settled.</summary>
    /// <returns>A warning when it could not be restored, otherwise null.</returns>
    Task<string?> ApplyReturnAudioAsync();

    /// <summary>Captures the Explorer anchor that a later restore needs.</summary>
    /// <returns>Whether the desktop can be safely taken over.</returns>
    Task<bool> PrepareExplorerExitAsync();

    /// <summary>Exits Explorer and waits for it, bounded.</summary>
    /// <returns>True when Explorer is confirmed gone.</returns>
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

/// <summary>
///     Enters Game Mode with one recovery path. Desktop requests are honoured between
///     operations, including after Explorer exit; a write already in flight settles before recovery.
///     Big Picture starts after the display layout, and the UI commit is awaited.
/// </summary>
/// <param name="backend">Borrowed effect owner; the transaction neither disposes it nor owns its services.</param>
/// <param name="launch">Launch configuration used throughout this attempt.</param>
internal sealed class GameModeEntryTransaction(IGameModeEntryBackend backend, GameModeLaunchConfiguration launch)
{
    /// <summary>Runs the entry.</summary>
    /// <param name="cancellationToken">Requests desktop recovery after any in-flight operation settles.</param>
    /// <returns>How the attempt ended.</returns>
    internal async Task<GameModeEntryResult> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginActionStepResult> entered = [];
        DisplayLayout? returnLayout = null;
        AudioProfilePreference? returnAudio = null;
        bool? recovered = null;

        // Recovery reports its own failure; cache it to avoid duplicate attempts and warnings.
        async Task<bool> RecoverAsync()
        {
            recovered ??= await backend.ReturnToDesktopAsync(returnLayout,
                PluginActionSequence.NeedsCompensation(entered)).ConfigureAwait(false);
            return recovered.Value;
        }

        try
        {
            backend.SetCancellable(true);
            if (!await backend.RestorePendingReturnAsync(cancellationToken).ConfigureAwait(false))
            {
                return new GameModeEntryResult(GameModeEntryOutcome.Failed,
                    "The recorded desktop display or audio state could not be restored. Game Mode entry was stopped.");
            }

            if (launch.Kind == GameModeLaunchKind.Custom)
            {
                backend.SetStatus("Recording the current display arrangement");
                returnLayout = launch.Return == GameModeReturn.DesktopLayout
                    ? launch.DesktopLayout
                    : Captured(await backend.ObserveAsync().ConfigureAwait(false));
            }

            // The desktop snapshot is taken wherever the return layout is taken. Returning falls
            // back to it whenever the Desktop profile has no audio preference of its own, so a
            // custom launch that sets no Game Mode audio still has to leave one behind.
            if (launch.Kind == GameModeLaunchKind.Custom || launch.GameAudio is not null)
            {
                returnAudio = await backend.CaptureAudioAsync(cancellationToken).ConfigureAwait(false);
            }

            if (launch.EnterActions.Count > 0)
            {
                backend.SetStatus("Running the configured entry actions");
                entered = await backend.RunEnterActionsAsync(launch.EnterActions, cancellationToken)
                    .ConfigureAwait(false);
                if (entered.FirstOrDefault(step => !step.Succeeded) is { } failed)
                {
                    return new GameModeEntryResult(GameModeEntryOutcome.Failed,
                        await RecoverAsync().ConfigureAwait(false)
                            ? $"Game Mode entry action: {failed.Detail}"
                            : null);
                }
            }

            var required = RequiredDisplays();
            if (required.Count > 0)
            {
                backend.SetStatus(required.Count == 1
                    ? $"Waiting for {required[0].FriendlyName}"
                    : $"Waiting for {required.Count} displays");
                await backend.WaitForDisplaysAsync(required, cancellationToken).ConfigureAwait(false);
            }

            if (launch.Kind == GameModeLaunchKind.Custom || returnAudio is not null)
            {
                backend.SetStatus("Preparing the display layout");
                await backend.PersistPendingReturnAsync(returnLayout, returnAudio).ConfigureAwait(false);
            }

            backend.SetStatus("Preparing the Windows desktop");
            var prepared = await backend.PrepareExplorerExitAsync().ConfigureAwait(false);
            if (!prepared)
            {
                return new GameModeEntryResult(GameModeEntryOutcome.DesktopPreserved,
                    await RecoverAsync().ConfigureAwait(false) ? SessionModes.ExplorerTakeoverRefusedWarning : null);
            }

            // A display can drop out again between the wait and here, and re-entering the wait is
            // cheaper and kinder than failing this far in.
            if (required.Count > 0)
            {
                var now = await backend.ObserveAsync().ConfigureAwait(false);
                if (DisplayArrivalWaiter.Missing(now, required).Count > 0)
                {
                    backend.SetStatus("A display disappeared again; still waiting");
                    await backend.WaitForDisplaysAsync(required, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ---- Boundary. Explorer is about to leave; cancellation stops being free. ----
            backend.SetCancellable(false);
            backend.SetStatus("Leaving the Windows desktop");
            var exited = await backend.ExitExplorerAndWaitAsync().ConfigureAwait(false);
            if (!exited)
            {
                return new GameModeEntryResult(GameModeEntryOutcome.DesktopPreserved,
                    await RecoverAsync().ConfigureAwait(false) ? SessionModes.ExplorerExitFailedWarning : null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? layoutWarning = null;
            if (launch is { Kind: GameModeLaunchKind.Custom, GameLayout: { } game })
            {
                backend.SetStatus("Applying the display layout");
                var applied = await backend.ApplyLayoutAsync(game, CancellationToken.None)
                    .ConfigureAwait(false);
                // A refused layout leaves Game Mode usable on the existing display; report the refusal.
                if (!applied.Applied)
                {
                    layoutWarning = "Game Mode display layout: " + DisplayText.Layout(applied);
                }
            }
            else
            {
                await backend.ApplyDefaultPostureAsync().ConfigureAwait(false);
            }

            if (launch.GameAudio is not null)
            {
                backend.SetStatus("Applying the Game Mode audio preferences");
                var audio = await backend.ApplyAudioAsync(launch.GameAudio, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!audio.Succeeded)
                {
                    // Both can go wrong in the same entry, and the display warning must not hide
                    // the audio one: past the boundary these lines are all the user gets.
                    var audioWarning = "Game Mode audio: " + string.Join(" ", audio.Operations
                        .Where(static operation => !operation.Succeeded)
                        .Select(static operation => operation.Name + " " + operation.Detail));
                    layoutWarning = layoutWarning is null ? audioWarning : layoutWarning + " " + audioWarning;
                }
            }

            backend.SetStatus("Starting Steam Big Picture");
            await backend.ArmSteamDetectionAsync().ConfigureAwait(false);
            var steamWarning = await backend.RequestBigPictureAsync().ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await backend.CommitGameModeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new GameModeEntryResult(GameModeEntryOutcome.Entered, layoutWarning ?? steamWarning);
        }
        catch (OperationCanceledException)
        {
            await RecoverAsync().ConfigureAwait(false);
            return new GameModeEntryResult(GameModeEntryOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            Log.Error("Game Mode entry failed", ex);
            return new GameModeEntryResult(GameModeEntryOutcome.Failed,
                await RecoverAsync().ConfigureAwait(false) ? "Game Mode entry failed: " + ex.Message : null);
        }
    }

    /// <summary>
    ///     Every display the entry has to see before it can proceed: whatever the user asked
    ///     to wait for, plus every display the layout is going to configure.
    /// </summary>
    private List<DisplayTargetIdentity> RequiredDisplays()
    {
        List<DisplayTargetIdentity> required = [];
        foreach (var target in new[] { launch.WaitForDisplay }.OfType<DisplayTargetIdentity>()
                     .Concat(launch.Kind == GameModeLaunchKind.Custom
                         ? launch.GameLayout?.Outputs.Select(output => output.Target) ?? []
                         : []))
        {
            if (!required.Exists(other => other.Matches(target)))
            {
                required.Add(target);
            }
        }

        return required;
    }

    private static DisplayLayout Captured(DisplayArrangement arrangement)
    {
        return new DisplayLayout([
            .. arrangement.Targets.Where(target => target is { Active: true, Current: not null })
                .Select(target => target.Current!)
        ]);
    }
}
