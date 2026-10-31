using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Everything the Game Mode entry and desktop return do through the session's existing owners.
///     Launch settings come from the session's current configuration.
/// </summary>
/// <param name="state">The session's live state.</param>
/// <param name="boot">The entry splash and the startup-app watch a takeover forgets.</param>
/// <param name="integrations">The graphics owner and the plugins that run the entry and leave actions.</param>
/// <param name="system">The audio the entry captures and the return restores.</param>
/// <param name="modes">Reads the session's mode coordinator.</param>
/// <param name="desktopHost">Reads the session's verified Explorer launch path.</param>
/// <param name="monitor">Reads the session's Steam monitor.</param>
/// <param name="displayChangeWindow">Reads the window that hears monitors arrive, when it exists.</param>
internal sealed class GameModeEntryBackend(
    SessionState state,
    SessionBoot boot,
    IntegrationServices integrations,
    SystemServices system,
    Func<SessionModes?> modes,
    Func<ExplorerDesktopHost?> desktopHost,
    Func<SteamMonitor?> monitor,
    Func<DisplayChangeWindow?> displayChangeWindow) : IGameModeEntryBackend
{
    private SessionModes Modes =>
        modes() ?? throw new InvalidOperationException("The session modes were not created.");

    private ExplorerDesktopHost DesktopHost =>
        desktopHost() ?? throw new InvalidOperationException("The Explorer host was not created.");

    /// <summary>
    ///     Restores the recorded desktop state through the live audio service, or through one built for this
    ///     restore while the session has none (before its services start, after they stop).
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the restore.</param>
    /// <param name="requireAudio">Whether unavailable audio holds recovery pending; entry preflight may proceed without it.</param>
    /// <returns>Whether everything recorded was restored.</returns>
    internal Task<bool> RestorePendingDesktopAsync(CancellationToken cancellationToken, bool requireAudio = true)
    {
        return GameModeReturnRecovery.RestorePendingAsync(state.Store, cancellationToken, system.AudioProfiles,
            applyGraphics: integrations.RestorePendingDisplayGpuAsync, requireAudio: requireAudio);
    }

    private DisplayArrivalWaiter CreateArrivalWaiter()
    {
        var window = displayChangeWindow();
        return new DisplayArrivalWaiter(
            DisplayLayouts.Observe,
            (backstop, token) => ShellDisplayChangeSignal.WaitForChangeAsync(window, backstop, token),
            Task.Delay);
    }

    /// <inheritdoc />
    public void SetStatus(string line)
    {
        Log.Info($"Game Mode entry: {line}.");
        Dispatcher.UIThread.Post(() =>
        {
            if (!state.ShutdownRequested)
            {
                boot.EnsureEntrySplash().SetStatus(line);
            }
        });
    }

    /// <inheritdoc />
    public async Task ArmSteamDetectionAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!state.ShutdownRequested)
            {
                boot.EnsureEntrySplash().ArmSteamDetection();
            }
        });
    }

    /// <inheritdoc />
    public void SetCancellable(bool cancellable)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (state.ShutdownRequested)
            {
                return;
            }

            boot.SetEntryCancellable(cancellable);
        });
    }

    /// <inheritdoc />
    public Task<DisplayArrangement> ObserveAsync()
    {
        return Task.Run(DisplayLayouts.Observe, state.ShutdownToken);
    }

    /// <inheritdoc />
    public async Task<DisplayArrangement> WaitForDisplaysAsync(
        IReadOnlyList<DisplayTargetIdentity> targets,
        CancellationToken cancellationToken)
    {
        Log.Info($"Display wait requested: {JsonSerializer.Serialize(targets)}");
        var elapsed = Stopwatch.StartNew();
        var observed =
            await CreateArrivalWaiter().WaitAsync(targets, cancellationToken).ConfigureAwait(false);
        Log.Info($"Display wait settled after {elapsed.ElapsedMilliseconds} ms: {observed.Fingerprint}");
        return observed;
    }

    /// <inheritdoc />
    public async Task<DisplayLayoutResult> ApplyLayoutAsync(
        DisplayLayout layout, CancellationToken cancellationToken)
    {
        var result = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DisplayLayoutDiagnostics.Apply(layout, DisplayLayouts.Apply,
                DisplayLayouts.Observe, Log.Info, Log.Warn);
        }, CancellationToken.None).ConfigureAwait(false);
        if (result.Applied)
        {
            // Observe route stability before HDMI audio and output-specific driver commands.
            // Windows acceptance already completed the layout write; this is not a value gate.
            using var settle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            settle.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await CreateArrivalWaiter().WaitAsync([], settle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warn(
                    "Display routes are still changing after the accepted layout; later controls validate their current routes.");
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task<AudioProfilePreference?> CaptureAudioAsync(CancellationToken cancellationToken)
    {
        return system.AudioProfiles?.CaptureAsync(cancellationToken)
               ?? Task.FromResult<AudioProfilePreference?>(null);
    }

    /// <inheritdoc />
    public Task<string?> ApplyDisplayGpuAsync(IReadOnlyList<DisplayGpuPreference> preferences,
        CancellationToken cancellationToken)
    {
        return DisplayGpuProfileService.ApplyAsync(integrations.Gpu, preferences, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DisplayGpuPreference>> CaptureDisplayGpuAsync(
        IReadOnlyList<DisplayGpuPreference> preferences, CancellationToken cancellationToken,
        bool refreshTopology = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (refreshTopology && integrations.Gpu is { } graphics)
        {
            await graphics.RefreshDisplayTopologyAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = integrations.Gpu?.DisplayCapabilities() ?? [];
        var captured = preferences
            .Select(preference => DisplayGpuProfileService.Resolve(preference, current))
            .OfType<DisplayGpuCapability>()
            .Where(capability => capability.ObservedValue is not null)
            .Select(capability => new DisplayGpuPreference
            {
                Target = capability.Target, PluginId = capability.PluginId,
                CapabilityId = capability.Descriptor!.CapabilityId,
                InstanceId = capability.Descriptor.InstanceId, Value = capability.ObservedValue!.Value
            }).ToArray();
        if (refreshTopology && captured.Length < preferences.Count)
        {
            Log.Warn("Display driver preferences: some original values are unreadable or unavailable. "
                     + "Their controls remain writable; automatic original-value restoration is limited to captured values and explicit Desktop preferences.");
        }

        return captured;
    }

    /// <inheritdoc />
    public Task<AudioProfileApplyResult> ApplyAudioAsync(
        AudioProfilePreference? preference,
        CancellationToken cancellationToken)
    {
        return system.AudioProfiles?.ApplyAsync(preference, cancellationToken)
               ?? Task.FromResult(new AudioProfileApplyResult([]));
    }

    /// <inheritdoc />
    public Task PersistPendingReturnAsync(DisplayLayout? layout, AudioProfilePreference? audio,
        IReadOnlyList<DisplayGpuPreference>? graphics = null)
    {
        return Task.Run(() =>
        {
            // The durable record is the one source of truth for the return; nothing is kept in memory.
            state.Store.Update(fresh =>
            {
                fresh.GameModeLaunchRecovery.PendingReturnLayout = layout;
                fresh.GameModeLaunchRecovery.PendingReturnAudio = audio;
                fresh.GameModeLaunchRecovery.PendingReturnDisplayGpu = graphics is null
                    ? []
                    : GameModeReturnRecovery.MergeGraphicsOriginals(
                        fresh.GameModeLaunchRecovery.PendingReturnDisplayGpu ?? [], graphics);
                fresh.GameModeLaunchRecovery.EnteredAt =
                    layout is null && audio is null
                                   && fresh.GameModeLaunchRecovery.PendingReturnDisplayGpu.Count == 0
                        ? null
                        : DateTimeOffset.UtcNow;
                return true;
            });
        });
    }

    /// <inheritdoc />
    public async Task<bool> RestorePendingReturnAsync(CancellationToken cancellationToken)
    {
        // Missing HDMI audio belongs after the new display layout, never at an entry gate.
        // Keep the old durable record until entry records the actual desktop or return succeeds.
        var restored = await RestorePendingDesktopAsync(cancellationToken, false)
            .ConfigureAwait(false);

        if (!restored)
        {
            // The entry stops before it changed anything, so no desktop return runs to let the
            // monitor watch Steam again.
            Dispatcher.UIThread.Post(() => { monitor()?.Paused = false; });
        }

        return restored;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(
        IReadOnlyList<PluginActionStep> steps, CancellationToken cancellationToken)
    {
        await integrations.CommonPluginStartup.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await integrations.ActionSequence()
            .RunUntilFailureAsync(steps, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PluginActionStepResult>> RunLeaveActionsAsync()
    {
        return integrations.ActionSequence()
            .RunAllAsync(state.Config.GameModeLaunch.LeaveActions, state.ShutdownToken);
    }

    /// <inheritdoc />
    public Task ApplyDefaultPostureAsync()
    {
        return Task.Run(Modes.ApplyGameModePosture);
    }

    /// <inheritdoc />
    public async Task<bool> PrepareExplorerExitAsync()
    {
        // The normal desktop can be recreated only if its current taskbar owner is captured while
        // it still exists. A contaminated or unknown shell is preserved instead.
        var preparation = await DesktopHost.PrepareForExplorerExitAsync(state.ShutdownToken)
            .ConfigureAwait(false);
        return preparation.Prepared;
    }

    /// <inheritdoc />
    public async Task<bool> ExitExplorerAndWaitAsync()
    {
        boot.ForgetDesktopAppRelaunches();
        try
        {
            return await DesktopHost.ExitExplorerAndWaitAsync(SessionModes.ExplorerExitTimeout,
                    state.ShutdownToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Explorer exit failed", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> ReturnToDesktopAsync(DisplayLayout? layout, bool runLeaveActions)
    {
        return Modes.DesktopReturn.RunAsync(layout, runLeaveActions);
    }

    /// <inheritdoc />
    public async Task<string?> RequestBigPictureAsync()
    {
        try
        {
            return await Modes.SteamLifecycle.RequestBigPictureWhilePausedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Starting Steam Big Picture during game-mode transition failed", ex);
            return SteamLifecycle.BigPictureStartFailedWarning;
        }
    }

    /// <inheritdoc />
    public async Task CommitGameModeAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(Modes.CommitGameMode);
    }

    /// <inheritdoc />
    public async Task<string?> ApplyReturnLayoutAsync(DisplayLayout? explicitLayout,
        GameModeLaunchRecovery recovery)
    {
        var launch = state.Config.GameModeLaunch;
        var layout = explicitLayout
                     ?? recovery.PendingReturnLayout
                     ?? (launch.Return == GameModeReturn.DesktopLayout ? launch.DesktopLayout : null);
        if (layout is null)
        {
            return null;
        }

        var result =
            await ApplyLayoutAsync(layout, CancellationToken.None).ConfigureAwait(false);
        return result.Applied ? null : "Desktop display layout: " + DisplayText.Layout(result);
    }

    /// <inheritdoc />
    public async Task<string?> ApplyReturnDisplayGpuAsync(GameModeLaunchRecovery recovery)
    {
        var desktopGpu = state.Config.GameModeLaunch.DesktopDisplayGpu;
        var warning = await ApplyDisplayGpuAsync(
            GameModeReturnRecovery.MergeGraphicsOriginals(desktopGpu, recovery.PendingReturnDisplayGpu ?? []),
            CancellationToken.None).ConfigureAwait(false);
        return warning is null ? null : "Desktop display controls: " + warning;
    }

    /// <inheritdoc />
    public async Task<string?> ApplyReturnAudioAsync(GameModeLaunchRecovery recovery)
    {
        var audio = GameModeLaunchRules.DesktopAudio(state.Config.GameModeLaunch,
            recovery);
        var result = await ApplyAudioAsync(audio, CancellationToken.None).ConfigureAwait(false);
        return result.Succeeded
            ? null
            : "Desktop audio: " + string.Join(" ", result.Operations
                .Where(static operation => !operation.Succeeded)
                .Select(static operation => operation.Name + " " + operation.Detail));
    }
}
