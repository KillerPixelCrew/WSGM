using System;
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

    internal static void RestoreBestEffort()
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            RestorePendingAsync(budget.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Recorded desktop recovery remains pending: {ex.Message}");
        }
    }

    internal static string PendingFingerprint()
    {
        return JsonSerializer.Serialize(ConfigStore.LoadForMutation().GameModeLaunchRecovery);
    }

    internal static void ClearRestored(string fingerprint)
    {
        ConfigStore.Mutate(fresh =>
        {
            if (JsonSerializer.Serialize(fresh.GameModeLaunchRecovery) == fingerprint)
            {
                fresh.GameModeLaunchRecovery = new GameModeLaunchRecovery();
            }
        });
    }

    internal static async Task<bool> RestorePendingAsync(CancellationToken cancellationToken,
        AudioProfileService? audio = null, Action<string>? report = null)
    {
        var work = RestoreUnderGateAsync(cancellationToken, audio, report ?? Log.Warn);
        work.ObserveFaults();
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RestoreUnderGateAsync(CancellationToken cancellationToken,
        AudioProfileService? audio, Action<string> report)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = ConfigStore.LoadForMutation();
            var pending = config.GameModeLaunchRecovery;
            if (pending.PendingReturnLayout is null && pending.PendingReturnAudio is null)
            {
                return true;
            }

            var fingerprint = JsonSerializer.Serialize(pending);
            var complete = true;
            if (pending.PendingReturnLayout is { } layout)
            {
                complete &= await AttemptAsync("display layout", async () =>
                        (await Task.Run(() => DisplayLayouts.Apply(layout), cancellationToken).ConfigureAwait(false))
                        .Applied)
                    .ConfigureAwait(false);
            }

            var preference = config.GameModeLaunch.DesktopAudio ?? pending.PendingReturnAudio;
            if (preference is not null)
            {
                complete &= await AttemptAsync("audio", async () =>
                {
                    if (audio is not null)
                    {
                        return (await audio.ApplyAsync(preference, cancellationToken).ConfigureAwait(false)).Succeeded;
                    }

                    await using AudioProfileService recoveryAudio = new();
                    return (await recoveryAudio.ApplyAsync(preference, cancellationToken).ConfigureAwait(false))
                        .Succeeded;
                }).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (complete && ExplorerControl.IsDesktopShellRunning())
            {
                ConfigStore.Mutate(fresh =>
                {
                    if (JsonSerializer.Serialize(fresh.GameModeLaunchRecovery) == fingerprint)
                    {
                        fresh.GameModeLaunchRecovery = new GameModeLaunchRecovery();
                    }
                });
            }

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
