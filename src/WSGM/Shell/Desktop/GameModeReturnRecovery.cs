using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Restores recorded desktop state without constructing UI or device services.</summary>
internal static class GameModeReturnRecovery
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Panic recovery: restores the record through the caller's audio service and clears what it restored.</summary>
    /// <param name="store">The configuration holding the record.</param>
    /// <param name="audio">The audio service the panic path built for this restore.</param>
    internal static void RestoreBestEffort(ConfigStore store, AudioProfileService audio)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var fingerprint = PendingFingerprint(store);
            if (RestorePendingAsync(store, budget.Token, audio).GetAwaiter().GetResult()
                && ExplorerControl.IsDesktopShellRunning())
            {
                ClearRestored(store, fingerprint);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Recorded desktop recovery remains pending: {ex.Message}");
        }
    }

    /// <summary>Captures the recovery record for a later conditional clear.</summary>
    /// <param name="store">Configuration store read synchronously.</param>
    /// <returns>Serialized recovery state used as a concurrency fingerprint.</returns>
    internal static string PendingFingerprint(ConfigStore store)
    {
        return Fingerprint(store.Read().RequireConfig().GameModeLaunchRecovery);
    }

    /// <summary>Clears recovery state only if it still matches the restored record.</summary>
    /// <param name="store">Configuration store mutated synchronously.</param>
    /// <param name="fingerprint">Fingerprint captured before restoration; a mismatch preserves the newer record.</param>
    internal static void ClearRestored(ConfigStore store, string fingerprint)
    {
        store.Update(fresh =>
        {
            if (Fingerprint(fresh.GameModeLaunchRecovery) != fingerprint)
            {
                return false;
            }

            fresh.GameModeLaunchRecovery = new GameModeLaunchRecovery();
            return true;
        });
    }

    private static string Fingerprint(GameModeLaunchRecovery recovery)
    {
        return JsonSerializer.Serialize(recovery, ConfigJsonContext.Tolerant.GameModeLaunchRecovery);
    }

    /// <summary>Retains the earliest captured original and adds newly visible output controls without losing unresolved debts.</summary>
    internal static List<DisplayGpuPreference> MergeGraphicsOriginals(
        IReadOnlyList<DisplayGpuPreference> originals, IReadOnlyList<DisplayGpuPreference> captured)
    {
        List<DisplayGpuPreference> merged = [.. originals];
        foreach (var preference in captured)
        {
            var matching = merged.Where(original => original.PluginId == preference.PluginId
                                                    && original.CapabilityId == preference.CapabilityId
                                                    && (preference.Target is null
                                                        ? original.Target is null
                                                        : original.Target?.Matches(preference.Target) == true))
                .ToArray();
            var exact = preference.Target is { DevicePath.Length: > 0 } target
                ? matching.Where(original => string.Equals(original.Target?.DevicePath, target.DevicePath,
                    StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            if (exact.Length == 1 || matching.Length == 1)
            {
                continue;
            }

            merged.Add(preference);
        }

        return merged;
    }

    /// <summary>Serializes recovery attempts and applies recorded display and audio state.</summary>
    /// <param name="store">Configuration store containing pending recovery.</param>
    /// <param name="cancellationToken">Cancels the caller’s wait and cooperative stages; admitted native work may continue.</param>
    /// <param name="audio">Borrowed audio service used after display restoration.</param>
    /// <param name="report">Failure reporter, or null to log warnings.</param>
    /// <param name="applyLayout">Optional layout writer; null uses Windows display control on a worker.</param>
    /// <param name="applyGraphics">Existing session graphics owner, or null to retain driver recovery for a later session.</param>
    /// <param name="requireAudio">
    ///     False only for entry preflight, where unavailable output controls and HDMI audio must not
    ///     gate their display activation.
    /// </param>
    /// <returns>True when no recovery is owed or all requested restores succeeded. The record is not cleared here.</returns>
    internal static async Task<bool> RestorePendingAsync(ConfigStore store, CancellationToken cancellationToken,
        AudioProfileService audio, Action<string>? report = null,
        Func<DisplayLayout, CancellationToken, Task<bool>>? applyLayout = null,
        Func<IReadOnlyList<DisplayGpuPreference>, CancellationToken, Task<bool>>? applyGraphics = null,
        bool requireAudio = true)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var work = RestoreUnderGateAsync(store, cancellationToken, audio, report ?? Log.Warn, applyLayout,
            applyGraphics, requireAudio);
        work.ObserveFaults();
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RestoreUnderGateAsync(ConfigStore store, CancellationToken cancellationToken,
        AudioProfileService audio, Action<string> report,
        Func<DisplayLayout, CancellationToken, Task<bool>>? applyLayout,
        Func<IReadOnlyList<DisplayGpuPreference>, CancellationToken, Task<bool>>? applyGraphics,
        bool requireAudio)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = store.Read().RequireConfig();
            var pending = config.GameModeLaunchRecovery;
            if (pending.PendingReturnLayout is null && pending.PendingReturnAudio is null
                                                    && pending.PendingReturnDisplayGpu is not { Count: > 0 })
            {
                return true;
            }

            var complete = true;
            if (pending.PendingReturnLayout is { } layout)
            {
                complete &= await AttemptAsync("display layout", () => applyLayout is not null
                    ? applyLayout(layout, cancellationToken)
                    : Task.Run(() => DisplayLayouts.Apply(layout).Applied, cancellationToken)).ConfigureAwait(false);
            }

            if (pending.PendingReturnDisplayGpu is { Count: > 0 }
                || config.GameModeLaunch.DesktopDisplayGpu.Count > 0)
            {
                var desktopGpu = config.GameModeLaunch.DesktopDisplayGpu;
                var graphicsRestored = await AttemptAsync("display driver controls", () => applyGraphics is not null
                    ? applyGraphics(MergeGraphicsOriginals(desktopGpu, pending.PendingReturnDisplayGpu ?? []),
                        cancellationToken)
                    : Task.FromResult(false)).ConfigureAwait(false);
                // The next layout and its entry actions may be what makes the old output available.
                // Preflight reports the debt and carries it forward rather than holding those actions.
                complete &= !requireAudio || graphicsRestored;
            }

            var preference = GameModeLaunchRules.DesktopAudio(config.GameModeLaunch, pending);
            if (preference is not null)
            {
                var audioRestored = await AttemptAsync("audio", async () =>
                        (await audio.ApplyAsync(preference, cancellationToken).ConfigureAwait(false)).Succeeded)
                    .ConfigureAwait(false);
                // A returned desktop layout can deliberately disable the HDMI display whose audio
                // was captured. Its absent endpoint must not prevent the next entry enabling it.
                // The caller keeps the recovery record until a later successful restore or snapshot.
                complete &= !requireAudio || audioRestored;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return complete;
        }
        finally
        {
            Gate.Release();
        }

        async Task<bool> AttemptAsync(string name, Func<Task<bool>> operation)
        {
            try
            {
                var restored = await operation().ConfigureAwait(false);
                if (!restored)
                {
                    report($"Desktop recovery: the recorded {name} remains pending.");
                }

                return restored;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report($"Desktop recovery: restoring {name} failed: {ex.Message}");
                return false;
            }
        }
    }
}
