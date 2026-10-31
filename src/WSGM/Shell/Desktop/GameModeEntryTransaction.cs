using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

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
        IReadOnlyList<DisplayGpuPreference> returnGraphics = [];
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

            if (launch.GameDisplayGpu.Count > 0)
            {
                returnGraphics = await backend.CaptureDisplayGpuAsync(launch.GameDisplayGpu, cancellationToken)
                    .ConfigureAwait(false);
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

            if (launch.Kind == GameModeLaunchKind.Custom || returnAudio is not null || launch.GameDisplayGpu.Count > 0)
            {
                backend.SetStatus("Preparing the display layout");
                await backend.PersistPendingReturnAsync(returnLayout, returnAudio, returnGraphics)
                    .ConfigureAwait(false);
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

            if (launch.GameDisplayGpu.Count > 0)
            {
                // A switched HDMI display may have been absent during the desktop snapshot.
                // Capture its controls only after entry actions and the layout make its route live,
                // while retaining every earlier original before any Game Mode driver write.
                var visibleOriginals = await backend.CaptureDisplayGpuAsync(launch.GameDisplayGpu,
                    CancellationToken.None, true).ConfigureAwait(false);
                returnGraphics = GameModeReturnRecovery.MergeGraphicsOriginals(returnGraphics, visibleOriginals);
                await backend.PersistPendingReturnAsync(returnLayout, returnAudio, returnGraphics)
                    .ConfigureAwait(false);
                backend.SetStatus("Applying the Game Mode display driver preferences");
                var warning = await backend.ApplyDisplayGpuAsync(launch.GameDisplayGpu, CancellationToken.None)
                    .ConfigureAwait(false);
                if (warning is not null)
                {
                    layoutWarning = (layoutWarning is null ? "" : layoutWarning + " ")
                                    + "Game Mode display controls: " + warning;
                }
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
