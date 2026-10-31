using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What both processor boost rows show for the current profile layers.</summary>
/// <param name="Effective">The layer's preference when one is set, else what Windows reports on AC, or null.</param>
/// <param name="Source">Which profile layer the preference came from.</param>
/// <param name="SourcesMatch">Whether Windows holds the same mode plugged in and on battery.</param>
internal sealed record CpuBoostView(CpuBoostMode? Effective, ProfileSource Source, bool SourcesMatch)
{
    /// <summary>The sentence both rows add about plugged-in and battery modes.</summary>
    internal string SourcesText => SourcesMatch
        ? "Applies to both plugged in and battery."
        : "Plugged in and battery currently differ; choosing sets both.";
}

/// <summary>Owns Windows' processor boost mode: its readback, WSGM's writes and the mode to restore.</summary>
/// <remarks>
///     Windows policy rather than device policy, so it runs with or without a device plugin. The per-game
///     reconciler, the overlay row and the Quick Access row all go through this one owner, so a change from
///     any of them is the same Windows write and the same profile save.
/// </remarks>
/// <param name="profiles">The profile owner a mode chosen by hand is saved to.</param>
/// <param name="cpuBoost">Windows' processor boost mode, or null when this session must not write it.</param>
internal sealed class CpuBoostService(ProfileService profiles, CpuBoost? cpuBoost)
{
    /// <summary>Guards every processor boost field and serializes the Windows write.</summary>
    private readonly Lock _gate = new();

    private CpuBoostMode? _baseline;
    private bool _imposed;
    private volatile CpuBoostStatus? _status;
    private bool _unsupportedLogged;

    /// <summary>Whether this session can read and write the processor boost mode at all.</summary>
    internal bool Available => cpuBoost is not null;

    /// <summary>The last processor boost readback or written mode, or null before the first read.</summary>
    internal CpuBoostStatus? Status => _status;

    /// <summary>Raised after the processor boost status changes, from whichever thread changed it.</summary>
    internal event Action? Changed;

    /// <summary>What the rows show for the given layers, or null until Windows reports a supported mode.</summary>
    /// <param name="layers">The current profile layers.</param>
    /// <returns>The effective mode, its source and whether the AC and battery modes agree.</returns>
    internal CpuBoostView? View(ProfileLayers layers)
    {
        if (_status is not { Supported: true } status)
        {
            return null;
        }

        var preference = layers.Value(values => values.CpuBoost);
        return new CpuBoostView(preference.Value ?? status.OnAc, preference.Source,
            status.OnAc == status.OnBattery);
    }

    /// <summary>Carries the running application's preferred mode to Windows, or restores the one found.</summary>
    /// <param name="effective">The mode the profile layers resolve to, or null.</param>
    /// <param name="applicationId">The running application, or null for the desktop, for the log line.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Completion after a due write was attempted.</returns>
    internal async Task ReconcileAsync(
        CpuBoostMode? effective,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        if (cpuBoost is null)
        {
            return;
        }

        PerAppCpuBoostDecision decision;
        lock (_gate)
        {
            decision = PerApplicationCpuBoostPolicy.DecideOnTargetChange(effective, _imposed, _baseline);
            if (decision.Action is not PerAppCpuBoostAction.Apply)
            {
                // Nothing preferred and nothing to take back. The imposed flag still clears, so a
                // mode that was never WSGM's to restore is not restored on some later transition.
                _imposed = false;
                return;
            }
        }

        if (await ApplyAsync(decision.Mode, effective is not null, cancellationToken).ConfigureAwait(false))
        {
            Log.Info(
                $"Per-application processor boost {CpuBoost.NameFor(decision.Mode)} for "
                + $"{applicationId ?? "the global profile"}.");
        }
    }

    /// <summary>Reads the processor boost mode from Windows and publishes it to both surfaces.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The readback, or null when this session has no processor boost.</returns>
    internal async Task<CpuBoostStatus?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (cpuBoost is null)
        {
            return null;
        }

        var status = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cpuBoost.Read();
        }, cancellationToken).ConfigureAwait(false);
        Publish(status);
        return status;
    }

    /// <summary>Applies a processor boost mode the user chose and saves it to the layer in force.</summary>
    /// <param name="mode">The mode the user chose.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the mode was written.</returns>
    /// <remarks>
    ///     The user-facing counterpart to the transition's own write. Saving happens here rather than
    ///     in a device hook, because Windows has no manual funnel of its own: both the overlay row and
    ///     the Quick Access row come through this one method.
    /// </remarks>
    internal async Task<bool> SetFromUserAsync(CpuBoostMode mode, CancellationToken cancellationToken)
    {
        if (!await ApplyAsync(mode, true, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var current = profiles.Current.Layers.Value(values => values.CpuBoost).Value;
        if (current != mode)
        {
            await profiles.SetAsync(values => values.CpuBoost = mode, $"CpuBoost={mode}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Writes one mode to Windows, keeping the mode found before WSGM's first write.</summary>
    /// <param name="mode">The mode to write.</param>
    /// <param name="imposed">
    ///     Whether WSGM owns the mode afterwards, so a later application without a preference restores the
    ///     baseline. Recorded under the same gate as the write.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the mode was written.</returns>
    private async Task<bool> ApplyAsync(CpuBoostMode mode, bool imposed, CancellationToken cancellationToken)
    {
        if (cpuBoost is null)
        {
            return false;
        }

        try
        {
            return await Task.Run(() =>
            {
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = cpuBoost.Read();
                    if (!before.Supported)
                    {
                        if (!_unsupportedLogged)
                        {
                            _unsupportedLogged = true;
                            Log.Warn("Processor boost mode is not exposed by the active power scheme.");
                        }

                        Publish(before);
                        return false;
                    }

                    if (!_imposed)
                    {
                        // The value to come back to once no layer prefers one. Null when Windows holds
                        // a mode WSGM does not offer, in which case nothing is restored.
                        _baseline = before.OnAc;
                    }

                    cpuBoost.Apply(mode, cancellationToken);
                    _imposed = imposed;
                    Publish(new CpuBoostStatus(true, mode, mode));
                    return true;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Processor boost {CpuBoost.NameFor(mode)} was not applied: {ex.Message}");
            return false;
        }
    }

    private void Publish(CpuBoostStatus status)
    {
        var previous = _status;
        _status = status;
        if (previous != status)
        {
            Changed?.Invoke();
        }
    }
}
