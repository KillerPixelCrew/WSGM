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

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>
    ///     Everything the Game Mode entry and desktop return do through the session's existing owners.
    ///     Launch settings come from the session's current configuration.
    /// </summary>
    private sealed class SessionEntryBackend(ShellSession session) : IGameModeEntryBackend
    {
        private SessionModes Modes =>
            session._modes ?? throw new InvalidOperationException("The session modes were not created.");

        private ExplorerDesktopHost DesktopHost =>
            session._desktopHost ?? throw new InvalidOperationException("The Explorer host was not created.");

        public void SetStatus(string line)
        {
            Log.Info($"Game Mode entry: {line}.");
            Dispatcher.UIThread.Post(() =>
            {
                if (!session._shutdownRequested)
                {
                    session.EnsureEntrySplash().SetStatus(line);
                }
            });
        }

        public async Task ArmSteamDetectionAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!session._shutdownRequested)
                {
                    session.EnsureEntrySplash().ArmSteamDetection();
                }
            });
        }

        public void SetCancellable(bool cancellable)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (session._shutdownRequested)
                {
                    return;
                }

                session._gameModeEntryActive = cancellable;
                session.EnsureEntrySplash().SetActionLabel(
                    cancellable ? "Cancel" : "Switch to desktop");
            });
        }

        public Task<DisplayArrangement> ObserveAsync()
        {
            return Task.Run(DisplayLayouts.Observe, session._shutdownCancellation.Token);
        }

        public async Task<DisplayArrangement> WaitForDisplaysAsync(
            IReadOnlyList<DisplayTargetIdentity> targets,
            CancellationToken cancellationToken)
        {
            Log.Info($"Display wait requested: {JsonSerializer.Serialize(targets)}");
            var elapsed = Stopwatch.StartNew();
            var observed =
                await session.CreateArrivalWaiter().WaitAsync(targets, cancellationToken).ConfigureAwait(false);
            Log.Info($"Display wait settled after {elapsed.ElapsedMilliseconds} ms: {observed.Fingerprint}");
            return observed;
        }

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
                    await session.CreateArrivalWaiter().WaitAsync([], settle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.Warn(
                        "Display routes are still changing after the accepted layout; later controls validate their current routes.");
                }
            }

            return result;
        }

        public Task<AudioProfilePreference?> CaptureAudioAsync(CancellationToken cancellationToken)
        {
            return session._audioProfiles?.CaptureAsync(cancellationToken)
                   ?? Task.FromResult<AudioProfilePreference?>(null);
        }

        public Task<string?> ApplyDisplayGpuAsync(IReadOnlyList<DisplayGpuPreference> preferences,
            CancellationToken cancellationToken)
        {
            return DisplayGpuProfileService.ApplyAsync(session._gpu, preferences, cancellationToken);
        }

        public async Task<IReadOnlyList<DisplayGpuPreference>> CaptureDisplayGpuAsync(
            IReadOnlyList<DisplayGpuPreference> preferences, CancellationToken cancellationToken,
            bool refreshTopology = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (refreshTopology && session._gpu is { } graphics)
            {
                await graphics.RefreshDisplayTopologyAsync(cancellationToken).ConfigureAwait(false);
            }

            var current = session._gpu?.DisplayCapabilities() ?? [];
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

        public Task<AudioProfileApplyResult> ApplyAudioAsync(
            AudioProfilePreference? preference,
            CancellationToken cancellationToken)
        {
            return session._audioProfiles?.ApplyAsync(preference, cancellationToken)
                   ?? Task.FromResult(new AudioProfileApplyResult([]));
        }

        public Task PersistPendingReturnAsync(DisplayLayout? layout, AudioProfilePreference? audio,
            IReadOnlyList<DisplayGpuPreference>? graphics = null)
        {
            return Task.Run(() =>
            {
                // The durable record is the one source of truth for the return; nothing is kept in memory.
                session._store.Update(fresh =>
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

        public async Task<bool> RestorePendingReturnAsync(CancellationToken cancellationToken)
        {
            // Missing HDMI audio belongs after the new display layout, never at an entry gate.
            // Keep the old durable record until entry records the actual desktop or return succeeds.
            var restored = await session.RestorePendingDesktopAsync(cancellationToken, false)
                .ConfigureAwait(false);

            if (!restored)
            {
                // The entry stops before it changed anything, so no desktop return runs to let the
                // monitor watch Steam again.
                Dispatcher.UIThread.Post(() => { session._monitor?.Paused = false; });
            }

            return restored;
        }

        public async Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(
            IReadOnlyList<PluginActionStep> steps, CancellationToken cancellationToken)
        {
            await session._commonPluginStartup.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await session.ActionSequence()
                .RunUntilFailureAsync(steps, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<PluginActionStepResult>> RunLeaveActionsAsync()
        {
            return session.ActionSequence()
                .RunAllAsync(session._config.GameModeLaunch.LeaveActions, session._shutdownCancellation.Token);
        }

        public Task ApplyDefaultPostureAsync()
        {
            return Task.Run(Modes.ApplyGameModePosture);
        }

        public async Task<bool> PrepareExplorerExitAsync()
        {
            // The normal desktop can be recreated only if its current taskbar owner is captured while
            // it still exists. A contaminated or unknown shell is preserved instead.
            var preparation = await DesktopHost.PrepareForExplorerExitAsync(session._shutdownCancellation.Token)
                .ConfigureAwait(false);
            return preparation.Prepared;
        }

        public async Task<bool> ExitExplorerAndWaitAsync()
        {
            session.ForgetDesktopAppRelaunches();
            try
            {
                return await DesktopHost.ExitExplorerAndWaitAsync(SessionModes.ExplorerExitTimeout,
                        session._shutdownCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Explorer exit failed", ex);
                return false;
            }
        }

        public Task<bool> ReturnToDesktopAsync(DisplayLayout? layout, bool runLeaveActions)
        {
            return Modes.ReturnToDesktopAsync(layout, runLeaveActions);
        }

        public async Task<string?> RequestBigPictureAsync()
        {
            try
            {
                return await Modes.RequestBigPictureWhilePausedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Starting Steam Big Picture during game-mode transition failed", ex);
                return SessionModes.BigPictureStartFailedWarning;
            }
        }

        public async Task CommitGameModeAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(Modes.CommitGameMode);
        }

        public async Task<string?> ApplyReturnLayoutAsync(DisplayLayout? explicitLayout,
            GameModeLaunchRecovery recovery)
        {
            var launch = session._config.GameModeLaunch;
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

        public async Task<string?> ApplyReturnDisplayGpuAsync(GameModeLaunchRecovery recovery)
        {
            var desktopGpu = session._config.GameModeLaunch.DesktopDisplayGpu;
            var warning = await ApplyDisplayGpuAsync(
                GameModeReturnRecovery.MergeGraphicsOriginals(desktopGpu, recovery.PendingReturnDisplayGpu ?? []),
                CancellationToken.None).ConfigureAwait(false);
            return warning is null ? null : "Desktop display controls: " + warning;
        }

        public async Task<string?> ApplyReturnAudioAsync(GameModeLaunchRecovery recovery)
        {
            var audio = GameModeLaunchRules.DesktopAudio(session._config.GameModeLaunch,
                recovery);
            var result = await ApplyAudioAsync(audio, CancellationToken.None).ConfigureAwait(false);
            return result.Succeeded
                ? null
                : "Desktop audio: " + string.Join(" ", result.Operations
                    .Where(static operation => !operation.Succeeded)
                    .Select(static operation => operation.Name + " " + operation.Detail));
        }
    }
}
