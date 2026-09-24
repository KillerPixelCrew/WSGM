using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Wizard;

/// <summary>The PawnIO operations the wizard needs; the real one is <see cref="PawnIoSetup" />.</summary>
internal interface IPawnIoHost
{
    /// <summary>Reads what is installed now.</summary>
    /// <returns>The status.</returns>
    PawnIoStatus Detect();

    /// <summary>Installs the pinned version.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Null on success, or the problem.</returns>
    Task<string?> InstallAsync(CancellationToken cancellationToken);

    /// <summary>Uninstalls the installed version.</summary>
    /// <param name="status">Current status.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Null on success, or the problem.</returns>
    Task<string?> UninstallAsync(PawnIoStatus status, CancellationToken cancellationToken);
}

/// <summary>How an install or replacement ended.</summary>
/// <param name="Problem">Null on success, or the problem.</param>
/// <param name="After">What is installed afterwards.</param>
/// <param name="LostPrevious">Whether the tester's earlier version was removed and nothing replaced it.</param>
internal sealed record LabPawnIoOutcome(string? Problem, PawnIoStatus After, bool LostPrevious)
{
    /// <summary>Whether PawnIO is installed and its driver answers.</summary>
    public bool Running => Problem is null && After is { InstalledVersion: not null, DeviceOpened: true };
}

/// <summary>
///     Installs, replaces and removes PawnIO for the wizard, keeping <see cref="LabMachineState" /> as the
///     one record of what the lab did.
/// </summary>
/// <remarks>
///     Each change is recorded before it starts. A fresh install may later be removed; a replacement may
///     not, because the driver it replaced belonged to the tester. The record lives outside any project,
///     so a copied project never offers removal on another machine, and an install that finished after
///     the window closed is picked up by <see cref="Reconcile" />.
/// </remarks>
internal sealed class LabPawnIo(LabMachineState state, IPawnIoHost host)
{
    /// <summary>The coordinator against the real PawnIO installer.</summary>
    /// <param name="state">Machine record.</param>
    /// <returns>The coordinator.</returns>
    public static LabPawnIo ForMachine(LabMachineState state)
    {
        return new LabPawnIo(state, new SetupHost());
    }

    /// <summary>Reads what is installed now.</summary>
    /// <returns>The status.</returns>
    public PawnIoStatus Detect()
    {
        return host.Detect();
    }

    /// <summary>Brings the record in line with what is installed, for a session that ended mid-change.</summary>
    /// <returns>The status read.</returns>
    public PawnIoStatus Reconcile()
    {
        var status = host.Detect();
        if (status.InstalledVersion is null && state.Read().PawnIoInstalledByLab)
        {
            state.Update(changes => changes with { PawnIoInstalledByLab = false });
        }

        return status;
    }

    /// <summary>Installs the pinned version on a machine without PawnIO.</summary>
    /// <param name="cancellationToken">Cancels the wait; the record then stays set for <see cref="Reconcile" />.</param>
    /// <returns>How it ended.</returns>
    public async Task<LabPawnIoOutcome> InstallAsync(CancellationToken cancellationToken)
    {
        state.Update(changes => changes with { PawnIoInstalledByLab = true });
        var problem = await host.InstallAsync(cancellationToken).ConfigureAwait(false);
        var after = host.Detect();
        if (after.InstalledVersion is null)
        {
            state.Update(changes => changes with { PawnIoInstalledByLab = false });
        }

        return new LabPawnIoOutcome(problem, after, false);
    }

    /// <summary>Replaces an older PawnIO with the pinned version, on the tester's explicit choice.</summary>
    /// <param name="current">What is installed now.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    /// <returns>How it ended; <see cref="LabPawnIoOutcome.LostPrevious" /> when the old one is gone and the new one failed.</returns>
    public async Task<LabPawnIoOutcome> ReplaceAsync(PawnIoStatus current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        state.Update(changes => changes with { PawnIoReplacedVersion = current.InstalledVersion });
        var removal = await host.UninstallAsync(current, cancellationToken).ConfigureAwait(false);
        if (removal is not null)
        {
            state.Update(changes => changes with { PawnIoReplacedVersion = null });
            return new LabPawnIoOutcome($"The old PawnIO could not be removed: {removal}", host.Detect(), false);
        }

        var problem = await host.InstallAsync(cancellationToken).ConfigureAwait(false);
        var after = host.Detect();
        return new LabPawnIoOutcome(problem, after, after.InstalledVersion is null);
    }

    /// <summary>Whether the lab may offer to remove PawnIO: only a fresh install it made, still present.</summary>
    /// <returns>True when removal may be offered.</returns>
    public bool CanOfferRemoval()
    {
        var changes = state.Read();
        return changes is { PawnIoInstalledByLab: true, PawnIoReplacedVersion: null }
               && host.Detect().InstalledVersion is not null;
    }

    /// <summary>Removes a PawnIO the lab installed.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Null on success, or the problem.</returns>
    public async Task<string?> RemoveAsync(CancellationToken cancellationToken)
    {
        if (!CanOfferRemoval())
        {
            return "Device Lab did not install this PawnIO, so it does not remove it.";
        }

        var problem = await host.UninstallAsync(host.Detect(), cancellationToken).ConfigureAwait(false);
        if (problem is null)
        {
            state.Update(changes => changes with { PawnIoInstalledByLab = false });
        }

        return problem;
    }

    private sealed class SetupHost : IPawnIoHost
    {
        public PawnIoStatus Detect()
        {
            return PawnIoSetup.Detect();
        }

        public Task<string?> InstallAsync(CancellationToken cancellationToken)
        {
            return PawnIoSetup.InstallAsync(cancellationToken);
        }

        public Task<string?> UninstallAsync(PawnIoStatus status, CancellationToken cancellationToken)
        {
            return PawnIoSetup.UninstallAsync(status, cancellationToken);
        }
    }
}
