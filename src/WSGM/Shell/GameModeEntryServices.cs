using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The half of the entry transaction the resident session owns: the saved configuration,
/// the splash, the display work and the plugin actions.
///
/// <see cref="SessionModes"/> owns the other half — Explorer, Steam and the game-mode surfaces —
/// and it is the only part a preview coordinator can run at all, which is why the split is here.
/// </summary>
internal interface IGameModeEntryServices
{
    /// <summary>Reads the launch configuration fresh, so Settings saved a moment ago is honoured.</summary>
    /// <returns>The configuration to enter with.</returns>
    GameModeLaunchConfiguration ReadLaunch();

    /// <summary>Shows one status line on the splash.</summary>
    /// <param name="line">What is happening now.</param>
    void SetStatus(string line);

    /// <summary>Arms splash dismissal and its timeout on the UI thread before Steam is requested.</summary>
    Task ArmSteamDetectionAsync();

    /// <summary>Switches the splash button between cancelling entry and leaving for the desktop.</summary>
    /// <param name="cancellable">True before the Explorer exit, false after it.</param>
    void SetCancellable(bool cancellable);

    /// <summary>Observes the current desktop arrangement off the UI thread.</summary>
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

    /// <summary>Runs the configured entry actions, stopping at the first failure.</summary>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>One result per step that ran.</returns>
    Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(CancellationToken cancellationToken);

    /// <summary>Runs every configured leave action, reporting failures.</summary>
    /// <returns>One result per step.</returns>
    Task<IReadOnlyList<PluginActionStepResult>> RunLeaveActionsAsync();

    /// <summary>Puts the desktop layout back when Game Mode ends: the layout recorded at entry, or
    /// the configured Desktop layout, whichever this configuration owes.</summary>
    /// <returns>A warning when it could not be restored, otherwise null.</returns>
    Task<string?> ApplyReturnLayoutAsync();
}

/// <summary>Joins the session's services to the Explorer, Steam and commit primitives
/// <see cref="SessionModes"/> owns, and remembers whether Explorer actually left so a failure after
/// that point can be recovered rather than reported.</summary>
internal sealed class SessionModesEntryBackend(SessionModes modes, ExplorerDesktopHost desktopHost)
    : IGameModeEntryBackend
{

    /// <inheritdoc />
    public void SetStatus(string line) => modes.GameModeEntryServices?.SetStatus(line);

    /// <inheritdoc />
    public Task ArmSteamDetectionAsync() =>
        modes.GameModeEntryServices?.ArmSteamDetectionAsync() ?? Task.CompletedTask;

    /// <inheritdoc />
    public void SetCancellable(bool cancellable) => modes.GameModeEntryServices?.SetCancellable(cancellable);

    /// <inheritdoc />
    public Task<DisplayArrangement> ObserveAsync() =>
        modes.GameModeEntryServices?.ObserveAsync() ?? Task.Run(DisplayLayouts.Observe);

    /// <inheritdoc />
    public Task<DisplayArrangement> WaitForDisplaysAsync(
        IReadOnlyList<DisplayTargetIdentity> targets, CancellationToken cancellationToken) =>
        modes.GameModeEntryServices is { } services
            ? services.WaitForDisplaysAsync(targets, cancellationToken)
            : ObserveAsync();

    /// <inheritdoc />
    public Task<DisplayLayoutResult> ApplyLayoutAsync(DisplayLayout layout, CancellationToken cancellationToken) =>
        modes.GameModeEntryServices?.ApplyLayoutAsync(layout, cancellationToken)
        ?? Task.FromResult(new DisplayLayoutResult(DisplayLayoutOutcome.Rejected, [], 0, false, false, [],
            "This session cannot change displays."));

    /// <inheritdoc />
    public Task PersistPendingReturnAsync(DisplayLayout? layout) =>
        modes.GameModeEntryServices?.PersistPendingReturnAsync(layout) ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task ApplyDefaultPostureAsync() => Task.Run(modes.ApplyGameModePosture);

    /// <inheritdoc />
    public Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(CancellationToken cancellationToken) =>
        modes.GameModeEntryServices?.RunEnterActionsAsync(cancellationToken)
        ?? Task.FromResult<IReadOnlyList<PluginActionStepResult>>([]);

    /// <inheritdoc />
    public async Task<bool> PrepareExplorerExitAsync()
    {
        // The normal desktop can be recreated only if its current taskbar owner is captured while
        // it still exists. A contaminated or unknown shell is preserved instead.
        ExplorerPreparationResult preparation = await desktopHost.PrepareForExplorerExitAsync()
            .ConfigureAwait(false);
        return preparation.Prepared;
    }

    /// <inheritdoc />
    public async Task<bool> ExitExplorerAndWaitAsync()
    {
        try
        {
            bool exited = await desktopHost.ExitExplorerAndWaitAsync(SessionModes.ExplorerExitTimeout)
                .ConfigureAwait(false);
            return exited;
        }
        catch (Exception ex)
        {
            Log.Error("Explorer exit failed", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> ReturnToDesktopAsync(DisplayLayout? layout, bool runLeaveActions) =>
        modes.ReturnToDesktopAsync(layout, runLeaveActions);

    /// <inheritdoc />
    public async Task<string?> RequestBigPictureAsync()
    {
        try
        {
            return await modes.RequestBigPictureWhilePausedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Starting Steam Big Picture during game-mode transition failed", ex);
            return SessionModes.BigPictureStartFailedWarning;
        }
    }

    /// <inheritdoc />
    public async Task CommitGameModeAsync() =>
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(modes.CommitGameMode);
}
