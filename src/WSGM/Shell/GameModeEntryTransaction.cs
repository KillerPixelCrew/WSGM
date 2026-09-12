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

    /// <summary>The user cancelled. The desktop is as it was.</summary>
    Cancelled,

    /// <summary>A step failed before anything irreversible. The desktop is as it was.</summary>
    Failed,

    /// <summary>Explorer could not be removed, so the desktop was deliberately kept.</summary>
    DesktopPreserved,
}

/// <summary>The result of one entry attempt.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Warning">User-facing warning, or null when there is nothing to say.</param>
internal sealed record GameModeEntryResult(GameModeEntryOutcome Outcome, string? Warning = null);

/// <summary>Everything the entry transaction can do to the machine. The transaction owns the order
/// and the compensation; the backend owns the effects.</summary>
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

    /// <summary>Applies a layout and confirms it by readback.</summary>
    /// <param name="layout">The layout to apply.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <returns>What happened.</returns>
    Task<DisplayLayoutResult> ApplyLayoutAsync(DisplayLayout layout, CancellationToken cancellationToken);

    /// <summary>Records, or clears, the layout this session owes the desktop.</summary>
    /// <param name="layout">The layout to restore later, or null to clear the record.</param>
    /// <returns>A task that completes once the record is on disk.</returns>
    Task PersistPendingReturnAsync(DisplayLayout? layout);

    /// <summary>Applies the scaling posture Default entry uses.</summary>
    void ApplyDefaultPosture();

    /// <summary>Runs the configured entry actions, stopping at the first failure.</summary>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>One result per step that ran.</returns>
    Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(CancellationToken cancellationToken);

    /// <summary>Runs every configured leave action, reporting failures.</summary>
    /// <returns>One result per step.</returns>
    Task<IReadOnlyList<PluginActionStepResult>> RunLeaveActionsAsync();

    /// <summary>Captures the Explorer anchor that a later restore needs.</summary>
    /// <returns>Whether the desktop can be safely taken over.</returns>
    Task<bool> PrepareExplorerExitAsync();

    /// <summary>Exits Explorer and waits for it, bounded.</summary>
    /// <returns>True when Explorer is confirmed gone.</returns>
    Task<bool> ExitExplorerAndWaitAsync();

    /// <summary>Whether the desktop must be kept because Explorer is, or may be, still there.</summary>
    /// <returns>True when Game Mode must not commit.</returns>
    Task<bool> MustPreserveDesktopAsync();

    /// <summary>Asks Steam for Big Picture once.</summary>
    /// <returns>A warning when it could not be started, otherwise null.</returns>
    Task<string?> RequestBigPictureAsync();

    /// <summary>Takes Steam back out of Big Picture.</summary>
    void ExitBigPicture();

    /// <summary>Brings up the game-mode surfaces and marks the session committed.</summary>
    void CommitGameMode();
}

/// <summary>Enters Game Mode as one cancellable transaction.
///
/// The order matters more than any single step. Everything before Explorer leaves is undoable, so
/// the user may cancel freely and a failure puts the desktop back exactly as it was. Once Explorer
/// is gone there is no desktop to return to cheaply, so that exit is the boundary: after it the
/// splash button stops saying Cancel and starts offering a way back to the desktop, and every later
/// failure compensates forwards instead of pretending nothing happened.
///
/// Big Picture is requested after Explorer leaves and after the layout is applied, which is the
/// opposite of what the desktop-only shell used to do. Requesting it first was a latency
/// optimisation worth having when Steam was not already running; here Steam is already up on the
/// desktop, the splash covers the whole transaction anyway, and a Big Picture window created before
/// the layout would be built on the wrong display at the wrong scaling.</summary>
internal sealed class GameModeEntryTransaction(IGameModeEntryBackend backend, GameModeLaunchConfiguration launch)
{
    /// <summary>Runs the entry.</summary>
    /// <param name="cancellationToken">Cancels the entry; honoured until the boundary.</param>
    /// <returns>How the attempt ended.</returns>
    internal async Task<GameModeEntryResult> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginActionStepResult> entered = [];
        DisplayLayout? returnLayout = null;
        try
        {
            backend.SetCancellable(true);

            if (launch.Kind == GameModeLaunchKind.Custom)
            {
                backend.SetStatus("Recording the current display arrangement");
                returnLayout = launch.Return == GameModeReturn.DesktopLayout
                    ? launch.DesktopLayout
                    : Captured(await backend.ObserveAsync().ConfigureAwait(false));
            }

            if (launch.EnterActions.Count > 0)
            {
                backend.SetStatus("Running the configured entry actions");
                entered = await backend.RunEnterActionsAsync(cancellationToken).ConfigureAwait(false);
                if (entered.FirstOrDefault(step => !step.Succeeded) is { } failed)
                {
                    await CompensateBeforeBoundaryAsync(entered, returnLayout).ConfigureAwait(false);
                    return new(GameModeEntryOutcome.Failed, $"Game Mode entry action: {failed.Detail}");
                }
            }

            IReadOnlyList<DisplayTargetIdentity> required = RequiredDisplays();
            if (required.Count > 0)
            {
                backend.SetStatus(required.Count == 1
                    ? $"Waiting for {required[0].FriendlyName}"
                    : $"Waiting for {required.Count} displays");
                await backend.WaitForDisplaysAsync(required, cancellationToken).ConfigureAwait(false);
            }

            if (launch.Kind == GameModeLaunchKind.Custom)
            {
                backend.SetStatus("Preparing the display layout");
                await backend.PersistPendingReturnAsync(returnLayout).ConfigureAwait(false);
            }

            backend.SetStatus("Preparing the Windows desktop");
            bool prepared = await backend.PrepareExplorerExitAsync().ConfigureAwait(false);
            if (!prepared)
            {
                await CompensateBeforeBoundaryAsync(entered, returnLayout).ConfigureAwait(false);
                return new(GameModeEntryOutcome.DesktopPreserved, SessionModes.ExplorerTakeoverRefusedWarning);
            }

            // A display can drop out again between the wait and here, and re-entering the wait is
            // cheaper and kinder than failing this far in.
            if (required.Count > 0)
            {
                DisplayArrangement now = await backend.ObserveAsync().ConfigureAwait(false);
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
            bool exited = await backend.ExitExplorerAndWaitAsync().ConfigureAwait(false);
            if (!exited && await backend.MustPreserveDesktopAsync().ConfigureAwait(false))
            {
                await CompensateBeforeBoundaryAsync(entered, returnLayout).ConfigureAwait(false);
                return new(GameModeEntryOutcome.DesktopPreserved, SessionModes.ExplorerExitFailedWarning);
            }

            string? layoutWarning = null;
            if (launch is { Kind: GameModeLaunchKind.Custom, GameLayout: { } game })
            {
                backend.SetStatus("Applying the display layout");
                DisplayLayoutResult applied = await backend.ApplyLayoutAsync(game, CancellationToken.None)
                    .ConfigureAwait(false);
                // Past the boundary a refused layout is not worth abandoning Game Mode over: the
                // session is usable on whatever the desktop is showing, and saying so is better
                // than tearing everything down again.
                if (!applied.Applied) { layoutWarning = "Game Mode display layout: " + applied.Detail; }
            }
            else
            {
                backend.ApplyDefaultPosture();
            }

            backend.SetStatus("Starting Steam Big Picture");
            string? steamWarning = await backend.RequestBigPictureAsync().ConfigureAwait(false);

            backend.CommitGameMode();
            return new(GameModeEntryOutcome.Entered, layoutWarning ?? steamWarning);
        }
        catch (OperationCanceledException)
        {
            await CompensateBeforeBoundaryAsync(entered, returnLayout).ConfigureAwait(false);
            return new(GameModeEntryOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            Log.Error("Game Mode entry failed", ex);
            await CompensateBeforeBoundaryAsync(entered, returnLayout).ConfigureAwait(false);
            return new(GameModeEntryOutcome.Failed, "Game Mode entry failed: " + ex.Message);
        }
    }

    /// <summary>Every display the entry has to see before it can proceed: whatever the user asked
    /// to wait for, plus every display the layout is going to configure.</summary>
    private IReadOnlyList<DisplayTargetIdentity> RequiredDisplays()
    {
        List<DisplayTargetIdentity> required = [];
        foreach (DisplayTargetIdentity target in new[] { launch.WaitForDisplay }.OfType<DisplayTargetIdentity>()
            .Concat(launch.Kind == GameModeLaunchKind.Custom
                ? launch.GameLayout?.Outputs.Select(output => output.Target) ?? []
                : []))
        {
            if (!required.Exists(other => other.Matches(target))) { required.Add(target); }
        }
        return required;
    }

    private static DisplayLayout Captured(DisplayArrangement arrangement) =>
        new([.. arrangement.Targets.Where(target => target is { Active: true, Current: not null })
            .Select(target => target.Current!)]);

    /// <summary>Undoes an entry that never crossed the boundary.
    ///
    /// The leave actions run whenever an entry action was dispatched or left uncertain, because the
    /// only thing that reverses "the switch was told to select this PC" is telling it to select the
    /// other one. A rejected action changed nothing and is not worth an IR burst.</summary>
    private async Task CompensateBeforeBoundaryAsync(
        IReadOnlyList<PluginActionStepResult> entered, DisplayLayout? returnLayout)
    {
        try
        {
            if (PluginActionSequence.NeedsCompensation(entered) && launch.LeaveActions.Count > 0)
            {
                backend.SetStatus("Undoing the entry actions");
                await backend.RunLeaveActionsAsync().ConfigureAwait(false);
            }
            if (returnLayout is not null)
            {
                DisplayLayoutResult restored = await backend
                    .ApplyLayoutAsync(returnLayout, CancellationToken.None).ConfigureAwait(false);
                if (!restored.Applied) { Log.Warn("Restoring the desktop layout: " + restored.Detail); }
            }
            await backend.PersistPendingReturnAsync(null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Compensating a cancelled Game Mode entry failed", ex);
        }
    }
}
